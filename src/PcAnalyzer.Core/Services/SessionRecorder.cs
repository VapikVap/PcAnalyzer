using Microsoft.Extensions.Logging;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Services;

/// <summary>
/// Orchestrates game sessions: reacts to game detection (Auto/Hybrid) or user commands (Manual/Hybrid),
/// buffers telemetry and frame times, persists them in batches (crash-safe), runs the analyzer when the
/// session ends and recovers sessions that were interrupted by a crash or power loss.
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly ITelemetryService _telemetry;
    private readonly IGameDetector _detector;
    private readonly ISessionStore _store;
    private readonly ISystemInfoProvider _system;
    private readonly ISettingsService _settings;
    private readonly PerformanceAnalyzer _analyzer;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private List<TelemetrySample> _pendingSamples = new();
    private List<FrameChunk> _pendingFrames = new();
    private readonly List<LiveEvent> _liveEvents = new();
    private readonly LiveEventDetector _liveDetector = new();
    private Timer? _flushTimer;
    private DateTime _lastSampleUtc;

    public GameSession? Active { get; private set; }
    public int? ActivePid { get; private set; }
    public bool IsRecording => Active != null;

    public event EventHandler<GameSession>? SessionStarted;
    /// <summary>Raised after the session was saved and analysed.</summary>
    public event EventHandler<GameSession>? SessionCompleted;
    public event EventHandler<string>? SessionDiscarded;
    public event EventHandler<LiveEvent>? LiveEventRaised;
    public event EventHandler? StateChanged;

    public SessionRecorder(ITelemetryService telemetry, IGameDetector detector, ISessionStore store, ISystemInfoProvider system,
        ISettingsService settings, PerformanceAnalyzer analyzer, ILogger<SessionRecorder> log)
    {
        _telemetry = telemetry;
        _detector = detector;
        _store = store;
        _system = system;
        _settings = settings;
        _analyzer = analyzer;
        _log = log;
    }

    public IReadOnlyList<LiveEvent> LiveEvents { get { lock (_gate) return _liveEvents.ToList(); } }

    public async Task InitializeAsync()
    {
        await RecoverUnfinishedAsync();
        _telemetry.Tick += OnTick;
        _detector.GameStarted += OnGameStarted;
        _detector.GameExited += OnGameExited;
        _flushTimer = new Timer(_ => _ = OnTimerAsync(), null, FlushInterval, FlushInterval);
        if (_detector.Current is { } g) OnGameStarted(this, g);
    }

    // ------------------------------------------------------------------ commands

    public async Task<GameSession> StartManualAsync(string gameName, RunningApp? app)
    {
        if (IsRecording) await StopAsync();
        var name = string.IsNullOrWhiteSpace(gameName) ? app?.Name ?? "Без названия" : gameName.Trim();
        int? pid = app?.Pid;
        string? exe = app?.ExeName;
        if (pid == null && _detector.Current is { } cur && string.Equals(cur.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            pid = cur.Pid; exe = cur.ExeName;
        }
        return await BeginAsync(name, exe, app?.ExePath, pid, SessionOrigin.Manual);
    }

    public async Task StopAsync()
    {
        GameSession? s;
        lock (_gate)
        {
            s = Active;
            if (s == null) return;
            Active = null;
            ActivePid = null;
        }
        _telemetry.HighFrequency = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await FlushAsync(s);
        await FinishAsync(s, SessionStatus.Completed);
    }

    public async Task RenameAsync(string name, string? label)
    {
        var s = Active;
        if (s == null) return;
        s.GameName = string.IsNullOrWhiteSpace(name) ? s.GameName : name.Trim();
        s.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        await _store.UpdateSessionAsync(s);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-runs analysis for a stored session (e.g. after thresholds changed).</summary>
    public async Task<GameSession?> ReanalyzeAsync(long id)
    {
        var s = await _store.GetSessionAsync(id);
        if (s == null || s.Status == SessionStatus.Recording) return s;
        await AnalyzeAndSaveAsync(s);
        return s;
    }

    // ------------------------------------------------------------------ detection

    private void OnGameStarted(object? sender, DetectedGame g)
    {
        if (!IsRecording) _telemetry.SetTarget(g.Pid, g.ExeName);
        if (_settings.Current.DetectionMode == DetectionMode.Manual || IsRecording) return;
        _ = Task.Run(async () =>
        {
            try { await BeginAsync(g.Name, g.ExeName, g.ExePath, g.Pid, SessionOrigin.AutoDetected); }
            catch (Exception ex) { _log.LogError(ex, "Failed to start auto session"); }
        });
    }

    private void OnGameExited(object? sender, DetectedGame g)
    {
        var s = Active;
        if (s != null && s.Origin == SessionOrigin.AutoDetected && ActivePid == g.Pid)
        {
            _ = Task.Run(async () =>
            {
                try { await StopAsync(); }
                catch (Exception ex) { _log.LogError(ex, "Failed to stop auto session"); }
            });
        }
        else if (!IsRecording)
        {
            _telemetry.SetTarget(null, null);
        }
    }

    // ------------------------------------------------------------------ recording

    private async Task<GameSession> BeginAsync(string name, string? exe, string? path, int? pid, SessionOrigin origin)
    {
        SystemInfo? hw = null;
        try { hw = await _system.GetAsync(); } catch (Exception ex) { _log.LogWarning(ex, "System info unavailable"); }

        var s = new GameSession
        {
            GameName = name,
            ExeName = exe,
            ExePath = path,
            StartUtc = DateTime.UtcNow,
            Status = SessionStatus.Recording,
            Origin = origin,
            Hardware = hw,
        };
        s.Id = await _store.CreateSessionAsync(s);

        lock (_gate)
        {
            _pendingSamples = new();
            _pendingFrames = new();
            _liveEvents.Clear();
            _liveDetector.Reset();
            Active = s;
            ActivePid = pid;
        }
        _telemetry.SetTarget(pid, exe);
        _telemetry.HighFrequency = true;
        _log.LogInformation("Session {Id} started: {Game} (pid {Pid}, {Origin})", s.Id, name, pid, origin);
        SessionStarted?.Invoke(this, s);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return s;
    }

    private void OnTick(object? sender, TelemetryTickEventArgs e)
    {
        GameSession? s;
        LiveEvent[] raised;
        lock (_gate)
        {
            s = Active;
            if (s == null) return;
            _pendingSamples.Add(e.Sample);
            if (e.FrameTimes.Length > 0) _pendingFrames.Add(new FrameChunk(e.Sample.TimestampUtc, e.FrameTimes));
            _lastSampleUtc = e.Sample.TimestampUtc;
            raised = _liveDetector.Process(e.Sample, e.FrameTimes, s.StartUtc, _settings.Current.Thresholds).ToArray();
            _liveEvents.AddRange(raised);
        }
        foreach (var ev in raised)
        {
            LiveEventRaised?.Invoke(this, ev);
            _ = SafeAsync(() => _store.AppendEventAsync(s.Id, ev));
        }
    }

    private async Task OnTimerAsync()
    {
        try
        {
            await FlushAsync();
            // A session bound to a process (auto or manual) ends when that process exits,
            // so desktop time after the game is not mixed into the game's statistics.
            if (Active is { } s && ActivePid is { } pid && !IsProcessAlive(pid))
            {
                _log.LogInformation("Target process {Pid} exited, stopping session {Id}", pid, s.Id);
                await StopAsync();
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Recorder timer failed"); }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }
        catch { return true; }                               // access denied → it exists
    }

    private Task FlushAsync() => Active is { } s ? FlushAsync(s) : Task.CompletedTask;

    private async Task FlushAsync(GameSession s)
    {
        List<TelemetrySample> samples;
        List<FrameChunk> frames;
        lock (_gate)
        {
            samples = _pendingSamples;
            frames = _pendingFrames;
            _pendingSamples = new();
            _pendingFrames = new();
        }
        if (samples.Count == 0 && frames.Count == 0) return;
        await _io.WaitAsync();
        try
        {
            await _store.AppendTelemetryAsync(s.Id, samples);
            await _store.AppendFramesAsync(s.Id, frames);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist telemetry for session {Id}", s.Id);
        }
        finally { _io.Release(); }
    }

    private async Task FinishAsync(GameSession s, SessionStatus status)
    {
        s.EndUtc = _lastSampleUtc > s.StartUtc ? _lastSampleUtc : DateTime.UtcNow;
        s.Status = status;

        if (s.Origin == SessionOrigin.AutoDetected && s.Duration.TotalSeconds < _settings.Current.MinAutoSessionSeconds)
        {
            await _store.DeleteSessionAsync(s.Id);
            _log.LogInformation("Session {Id} discarded: too short ({Sec:0}s)", s.Id, s.Duration.TotalSeconds);
            SessionDiscarded?.Invoke(this, s.GameName);
            if (_detector.Current == null) _telemetry.SetTarget(null, null);
            return;
        }

        await AnalyzeAndSaveAsync(s);
        _log.LogInformation("Session {Id} finished: {Diag}", s.Id, s.Summary?.Diagnosis);
        if (_detector.Current is { } g) _telemetry.SetTarget(g.Pid, g.ExeName);
        else _telemetry.SetTarget(null, null);
        SessionCompleted?.Invoke(this, s);
    }

    private async Task AnalyzeAndSaveAsync(GameSession s)
    {
        try
        {
            var samples = await _store.LoadTelemetryAsync(s.Id);
            var frames = await _store.LoadFramesAsync(s.Id);
            var (summary, report) = await Task.Run(() => _analyzer.Analyze(new AnalysisInput
            {
                Session = s,
                Samples = samples,
                Frames = frames,
                Thresholds = _settings.Current.Thresholds,
                Hardware = s.Hardware,
            }));
            s.Summary = summary;
            s.Report = report;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Analysis failed for session {Id}", s.Id);
        }
        await _store.UpdateSessionAsync(s);
    }

    private async Task RecoverUnfinishedAsync()
    {
        try
        {
            foreach (var s in await _store.GetUnfinishedAsync())
            {
                var samples = await _store.LoadTelemetryAsync(s.Id);
                s.EndUtc = samples.Count > 0 ? samples[^1].TimestampUtc : s.StartUtc;
                s.Status = SessionStatus.Interrupted;
                s.Notes = (s.Notes + " Запись прервана (аварийное завершение приложения или ПК); проанализированы сохранённые данные.").Trim();
                await AnalyzeAndSaveAsync(s);
                _log.LogWarning("Recovered interrupted session {Id} ({Game})", s.Id, s.GameName);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recovery of unfinished sessions failed");
        }
    }

    private async Task SafeAsync(Func<Task> f)
    {
        try { await f(); } catch (Exception ex) { _log.LogWarning(ex, "Background store write failed"); }
    }

    /// <summary>Called on application exit: closes the session cleanly.</summary>
    public async ValueTask DisposeAsync()
    {
        _telemetry.Tick -= OnTick;
        _detector.GameStarted -= OnGameStarted;
        _detector.GameExited -= OnGameExited;
        _flushTimer?.Dispose();
        if (IsRecording)
        {
            try { await StopAsync(); } catch (Exception ex) { _log.LogError(ex, "Stop on exit failed"); }
        }
    }
}
