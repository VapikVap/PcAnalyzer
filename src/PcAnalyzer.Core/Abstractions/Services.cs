using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Abstractions;

public sealed class TelemetryTickEventArgs : EventArgs
{
    public required TelemetrySample Sample { get; init; }
    /// <summary>Frame times captured since the previous tick (empty if no frame source is attached).</summary>
    public float[] FrameTimes { get; init; } = Array.Empty<float>();
}

/// <summary>State of each data source, shown to the user so nothing is silently missing.</summary>
public sealed class SourceStatus
{
    public string Name { get; init; } = "";
    public bool Available { get; set; }
    public string Message { get; set; } = "";
}

public interface ITelemetryService : IAsyncDisposable
{
    event EventHandler<TelemetryTickEventArgs>? Tick;
    TelemetrySample? Latest { get; }
    IReadOnlyList<SourceStatus> Sources { get; }
    bool IsElevated { get; }
    /// <summary>Process whose CPU/RAM/GPU usage and frames are tracked (the game).</summary>
    void SetTarget(int? pid, string? exeName);
    int? TargetPid { get; }
    /// <summary>True while a session is recorded: full polling rate even in power-saving mode.</summary>
    bool HighFrequency { get; set; }
    IFrameTimeProvider FrameProvider { get; }
    void Start();
}

public enum FrameSourceState { Disabled, NotConfigured, Waiting, Running, Error }

public interface IFrameTimeProvider : IDisposable
{
    string Name { get; }
    FrameSourceState State { get; }
    string StatusMessage { get; }
    event EventHandler? StateChanged;
    void Attach(int pid, string exeName);
    void Detach();
    /// <summary>Moves captured frame times (ms) into <paramref name="into"/>; returns the count.</summary>
    int Drain(List<float> into);
}

public sealed record DetectedGame(int Pid, string Name, string ExeName, string? ExePath, string Reason);

public sealed record RunningApp(int Pid, string Name, string ExeName, string? ExePath, string? WindowTitle);

public interface IGameDetector
{
    event EventHandler<DetectedGame>? GameStarted;
    event EventHandler<DetectedGame>? GameExited;
    DetectedGame? Current { get; }
    bool Enabled { get; set; }
    IReadOnlyList<RunningApp> GetRunningApps();
}

public interface ISystemInfoProvider
{
    Task<SystemInfo> GetAsync(bool refresh = false);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    void Save();
}

public interface ISessionStore
{
    Task<long> CreateSessionAsync(GameSession session);
    Task UpdateSessionAsync(GameSession session);
    Task AppendTelemetryAsync(long sessionId, IReadOnlyList<TelemetrySample> samples);
    Task AppendFramesAsync(long sessionId, IReadOnlyList<FrameChunk> chunks);
    Task AppendEventAsync(long sessionId, LiveEvent ev);
    Task<GameSession?> GetSessionAsync(long id);
    Task<List<GameSession>> ListSessionsAsync(string? game = null, int limit = 500);
    Task<List<string>> ListGamesAsync();
    Task<List<TelemetrySample>> LoadTelemetryAsync(long sessionId);
    Task<List<FrameChunk>> LoadFramesAsync(long sessionId);
    Task<List<LiveEvent>> LoadEventsAsync(long sessionId);
    Task<List<GameSession>> GetUnfinishedAsync();
    Task DeleteSessionAsync(long id);
    Task<RetentionResult> RunRetentionAsync(AppSettings settings);
    long DatabaseSizeBytes { get; }
}

public sealed record RetentionResult(int SessionsDeleted, int RawDataPurged);

/// <summary>Something noticed while a session is being recorded.</summary>
public sealed class LiveEvent
{
    public DateTime TimestampUtc { get; set; }
    public double OffsetSec { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public Severity Severity { get; set; }
}
