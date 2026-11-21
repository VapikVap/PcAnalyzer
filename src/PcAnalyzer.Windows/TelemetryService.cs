using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Windows.Frames;
using PcAnalyzer.Windows.Processes;
using PcAnalyzer.Windows.Sensors;

namespace PcAnalyzer.Windows;

/// <summary>
/// Hardware monitoring service. Polls all sources on a dedicated low-priority background thread,
/// merges them into one <see cref="TelemetrySample"/> and raises <see cref="Tick"/>.
/// Every source is isolated: a failing source never stops the others and is re-opened automatically.
/// </summary>
public sealed class TelemetryService : ITelemetryService
{
    private readonly ISettingsService _settings;
    private readonly ISystemInfoProvider _system;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private PdhSystemSource? _pdh;
    private LhmSensorSource? _lhm;
    private ProcessMonitor? _proc;
    private double? _vramTotalFallback;

    private volatile bool _targetChanged;
    private int? _targetPid;
    private string? _targetExe;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastProcScan = DateTime.MinValue;
    private int _pdhFailures;

    private readonly SourceStatus _srcCounters = new() { Name = "Счётчики Windows (CPU, GPU, RAM, диск, сеть)" };
    private readonly SourceStatus _srcSensors = new() { Name = "Сенсоры GPU (температура, частоты, питание)" };
    private readonly SourceStatus _srcCpuTemp = new() { Name = "Температура CPU" };
    private readonly SourceStatus _srcFrames = new() { Name = "FPS / время кадра (PresentMon)" };
    private readonly SourceStatus _srcGame = new() { Name = "Процесс игры" };

    public event EventHandler<TelemetryTickEventArgs>? Tick;
    public TelemetrySample? Latest { get; private set; }
    public IReadOnlyList<SourceStatus> Sources => new[] { _srcCounters, _srcSensors, _srcCpuTemp, _srcFrames, _srcGame };
    public bool IsElevated { get; }
    public int? TargetPid => _targetPid;
    public bool HighFrequency { get; set; }
    public IFrameTimeProvider FrameProvider { get; }
    public IReadOnlyDictionary<int, double> GpuByPid => _pdh?.GpuByPid ?? new Dictionary<int, double>();

    public TelemetryService(ISettingsService settings, IFrameTimeProvider frames, ISystemInfoProvider system, ILogger<TelemetryService> log)
    {
        _settings = settings;
        _system = system;
        _log = log;
        FrameProvider = frames;
        using (var id = WindowsIdentity.GetCurrent()) IsElevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        _settings.Changed += (_, _) => OnSettingsChanged();
        frames.StateChanged += (_, _) => UpdateFrameStatus();
        UpdateFrameStatus();
        _srcGame.Message = "Игра не выбрана";
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Telemetry", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        _ = Task.Run(async () =>
        {
            try { _vramTotalFallback = (await _system.GetAsync()).PrimaryGpu?.VramMb; }
            catch (Exception ex) { _log.LogWarning(ex, "System info for VRAM total failed"); }
        });
    }

    public void SetTarget(int? pid, string? exeName)
    {
        if (pid == _targetPid) return;
        _targetPid = pid;
        _targetExe = exeName;
        _targetChanged = true;
        if (pid is { } p) FrameProvider.Attach(p, exeName ?? "");
        else FrameProvider.Detach();
        _srcGame.Message = pid.HasValue ? $"{exeName} (PID {pid})" : "Игра не выбрана";
        _srcGame.Available = pid.HasValue;
    }

    private void OnSettingsChanged()
    {
        if (FrameProvider is PresentMonProvider pm)
        {
            if (_targetPid is { } p) pm.Attach(p, _targetExe ?? "");
            else pm.RefreshConfiguration();
        }
    }

    private void UpdateFrameStatus()
    {
        _srcFrames.Available = FrameProvider.State == FrameSourceState.Running || FrameProvider.State == FrameSourceState.Waiting;
        _srcFrames.Message = FrameProvider.StatusMessage;
    }

    private int IntervalMs()
    {
        var s = _settings.Current;
        int ms = Math.Clamp(s.SampleIntervalMs, 250, 5000);
        if (s.PowerSavingMonitoring && !HighFrequency) ms = Math.Max(ms, 2000);
        return ms;
    }

    private void Loop()
    {
        try
        {
            _pdh = new PdhSystemSource(_log);
            _lhm = new LhmSensorSource(_log);
            _proc = new ProcessMonitor(_log);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Telemetry sources failed to initialize");
        }

        var sw = new Stopwatch();
        var frames = new List<float>(1024);
        while (!_cts.IsCancellationRequested)
        {
            sw.Restart();
            try { DoTick(frames); }
            catch (Exception ex) { _log.LogError(ex, "Telemetry tick failed"); }
            int wait = IntervalMs() - (int)sw.ElapsedMilliseconds;
            if (_cts.Token.WaitHandle.WaitOne(Math.Max(50, wait))) break;
        }
    }

    private void DoTick(List<float> frames)
    {
        var now = DateTime.UtcNow;
        var s = new TelemetrySample { TimestampUtc = now };

        if (_targetChanged)
        {
            _targetChanged = false;
            _proc?.SetTarget(_targetPid);
        }

        // Windows counters (with automatic re-open after repeated failures)
        if (_pdh != null)
        {
            bool ok = _pdh.Fill(s, _targetPid);
            _pdhFailures = ok ? 0 : _pdhFailures + 1;
            if (_pdhFailures >= 5) { _log.LogWarning("Re-opening PDH query"); _pdh.Reopen(); _pdhFailures = 0; }
            _srcCounters.Available = ok && _pdh.CpuAvailable;
            _srcCounters.Message = _pdh.GpuAvailable ? _pdh.Status : _pdh.Status + " · счётчики GPU недоступны (нужен WDDM 2.0+)";
        }

        // Hardware sensors
        if (_lhm != null)
        {
            _lhm.Fill(s);
            _srcSensors.Available = _lhm.HasGpuSensors;
            _srcSensors.Message = _lhm.HasGpuSensors ? $"{_lhm.Status}: {_lhm.GpuName}" : "Сенсоры видеокарты не найдены (" + _lhm.Status + ")";
            _srcCpuTemp.Available = _lhm.HasCpuTemp;
            _srcCpuTemp.Message = _lhm.HasCpuTemp ? "Работает"
                : IsElevated ? "Недоступна: нет драйвера доступа к сенсорам CPU (PawnIO) или процессор не поддерживается"
                             : "Недоступна без прав администратора";
        }
        if (s.VramTotalMb == null && _vramTotalFallback is > 0) s.VramTotalMb = _vramTotalFallback;

        // Game process + top processes
        if (_proc != null)
        {
            _proc.FillTarget(s);
            int scanEvery = _settings.Current.PowerSavingMonitoring && !HighFrequency ? 15 : 5;
            if ((now - _lastProcScan).TotalSeconds >= scanEvery)
            {
                _lastProcScan = now;
                try { s.TopProcesses = _proc.ScanTop(); } catch (Exception ex) { _log.LogDebug(ex, "Process scan failed"); }
            }
        }

        // Frames since the previous tick
        frames.Clear();
        FrameProvider.Drain(frames);
        if (frames.Count > 0)
        {
            double sum = 0, max = 0;
            foreach (var f in frames) { sum += f; if (f > max) max = f; }
            s.FrameCount = frames.Count;
            s.FrameTimeAvgMs = Math.Round(sum / frames.Count, 2);
            s.FrameTimeMaxMs = Math.Round(max, 2);
            s.Fps = Math.Round(frames.Count * 1000.0 / sum, 1);
        }
        UpdateFrameStatus();

        _lastTick = now;
        Latest = s;
        Tick?.Invoke(this, new TelemetryTickEventArgs { Sample = s, FrameTimes = frames.Count > 0 ? frames.ToArray() : Array.Empty<float>() });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_thread != null) await Task.Run(() => _thread.Join(3000));
        FrameProvider.Dispose();
        _lhm?.Dispose();
        _pdh?.Dispose();
        _proc?.Dispose();
    }
}
