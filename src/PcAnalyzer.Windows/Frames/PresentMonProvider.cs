using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Windows.Frames;

/// <summary>
/// Frame-time source based on Intel PresentMon (MIT, https://github.com/GameTechDev/PresentMon).
/// PresentMon reads the public DXGI/D3D9/Vulkan present ETW events. The installer ships the official console
/// build in &lt;app&gt;\PresentMon; a path in Settings overrides it. Requires admin rights or membership in
/// the "Performance Log Users" group.
/// </summary>
public sealed class PresentMonProvider : IFrameTimeProvider
{
    private const string SessionName = "PcAnalyzer";
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<float> _frames = new();
    private readonly object _gate = new();

    private Process? _proc;
    private int _pid;
    private string? _lastError;

    // Several swap chains (overlays, launchers) can present for the same process; keep the busiest.
    private readonly Dictionary<string, int> _swapCounts = new();
    private string? _dominant;
    private DateTime _swapWindowStart = DateTime.UtcNow;

    public string Name => "PresentMon";
    public FrameSourceState State { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public event EventHandler? StateChanged;

    public PresentMonProvider(Func<AppSettings> settings, ILogger<PresentMonProvider> log)
    {
        _settings = settings;
        _log = log;
        RefreshConfiguration();
    }

    public void RefreshConfiguration()
    {
        if (_proc != null) return;
        var cfg = _settings();
        if (cfg.FrameSource == FrameSourceKind.None) Set(FrameSourceState.Disabled, "Источник FPS отключён в настройках");
        else if (!IsConfigured(out var why)) Set(FrameSourceState.NotConfigured, why);
        else Set(FrameSourceState.Waiting, "Готов, ожидает игру");
    }

    private bool IsConfigured(out string why) => ResolvePath(out why) != null;

    /// <summary>
    /// The path from Settings wins; if it is empty or the file is gone, fall back to the copy shipped
    /// with the installer (<c>&lt;app&gt;\PresentMon\PresentMon*.exe</c>).
    /// </summary>
    private string? ResolvePath(out string why)
    {
        why = "";
        var path = _settings().PresentMonPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        if (FindBundled() is { } bundled) return bundled;
        why = string.IsNullOrWhiteSpace(path)
            ? "Источник FPS не подключён: укажите PresentMon в настройках"
            : "Файл PresentMon не найден: " + path;
        return null;
    }

    private static string? FindBundled()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "PresentMon");
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "PresentMon*.exe").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    public void Attach(int pid, string exeName)
    {
        Detach();
        var cfg = _settings();
        if (cfg.FrameSource == FrameSourceKind.None) { RefreshConfiguration(); return; }
        if (ResolvePath(out var why) is not { } exe) { Set(FrameSourceState.NotConfigured, why); return; }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--process_id", pid.ToString(CultureInfo.InvariantCulture), "--output_stdout", "--no_console_stats",
                                       "--session_name", SessionName, "--stop_existing_session", "--terminate_on_proc_exit" })
                psi.ArgumentList.Add(a);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            int[]? columns = null; // [processId, swapChain, frameTime]
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 } line) return;
                if (columns == null) { columns = ParseHeader(line); if (columns == null) Fail("Неизвестный формат вывода PresentMon"); return; }
                OnRow(line, columns);
            };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data.Trim(); };
            p.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (_proc != p) return;
                    _proc = null;
                }
                int code = SafeExitCode(p);
                if (code != 0)
                    Fail($"PresentMon завершился с кодом {code}: {_lastError ?? "нет подробностей"}. Нужны права администратора или группа «Пользователи журналов производительности».");
                else
                    Set(FrameSourceState.Waiting, "Игра закрыта, ожидание");
            };
            lock (_gate)
            {
                _pid = pid;
                _proc = p;
                _swapCounts.Clear();
                _dominant = null;
                _lastError = null;
            }
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Set(FrameSourceState.Running, $"Запись кадров: {exeName}");
            _log.LogInformation("PresentMon attached to {Pid}", pid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PresentMon start failed");
            Fail("Не удалось запустить PresentMon: " + ex.Message);
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static int[]? ParseHeader(string header)
    {
        var cols = header.Split(',').Select(c => c.Trim()).ToList();
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                int i = cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        int pid = Find("ProcessID");
        int swap = Find("SwapChainAddress");
        int ft = Find("MsBetweenPresents", "FrameTime");
        return pid < 0 || ft < 0 ? null : new[] { pid, swap, ft };
    }

    private void OnRow(string line, int[] c)
    {
        var parts = line.Split(',');
        if (parts.Length <= Math.Max(c[0], c[2])) return;
        if (!int.TryParse(parts[c[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid != _pid) return;
        if (!float.TryParse(parts[c[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) return;
        string swap = c[1] >= 0 && parts.Length > c[1] ? parts[c[1]] : "";

        lock (_gate)
        {
            _swapCounts[swap] = _swapCounts.GetValueOrDefault(swap) + 1;
            if ((DateTime.UtcNow - _swapWindowStart).TotalSeconds >= 2)
            {
                _dominant = _swapCounts.OrderByDescending(kv => kv.Value).First().Key;
                _swapCounts.Clear();
                _swapWindowStart = DateTime.UtcNow;
            }
            if (_dominant != null && swap != _dominant) return;
        }
        _frames.Enqueue(ms);
    }

    public int Drain(List<float> into)
    {
        int n = 0;
        while (_frames.TryDequeue(out var f)) { into.Add(f); n++; }
        return n;
    }

    public void Detach()
    {
        Process? p;
        lock (_gate) { p = _proc; _proc = null; }
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (Exception ex) { _log.LogDebug(ex, "PresentMon kill"); }
            p.Dispose();
        }
        while (_frames.TryDequeue(out _)) { }
        RefreshConfiguration();
    }

    private void Fail(string msg)
    {
        _log.LogWarning("PresentMon: {Msg}", msg);
        Set(FrameSourceState.Error, msg);
    }

    private void Set(FrameSourceState s, string msg)
    {
        State = s;
        StatusMessage = msg;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Detach();
}
