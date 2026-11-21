using Microsoft.Extensions.Logging.Abstractions;
using PcAnalyzer.Core.Abstractions;
using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;
using PcAnalyzer.Core.Services;
using PcAnalyzer.Data;

namespace PcAnalyzer.Core.Tests;

/// <summary>End-to-end: recorder → SQLite → analyzer, including crash recovery.</summary>
public class RecorderTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pca-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) try { File.Delete(f); } catch { }
    }

    private SqliteSessionStore Store() => new(_db, NullLogger<SqliteSessionStore>.Instance);

    private static SessionRecorder Recorder(FakeTelemetry t, FakeDetector d, ISessionStore store, AppSettings? s = null) =>
        new(t, d, store, new FakeSystem(), new FakeSettings(s ?? new AppSettings()), new PerformanceAnalyzer(), NullLogger<SessionRecorder>.Instance);

    [Fact]
    public async Task ManualSession_IsPersistedAnalysedAndReloadable()
    {
        var store = Store();
        var tel = new FakeTelemetry();
        var rec = Recorder(tel, new FakeDetector(), store);
        await rec.InitializeAsync();

        var s = await rec.StartManualAsync("My Game", new RunningApp(1234, "My Game", "mygame.exe", null, "My Game"));
        Assert.Equal(1234, tel.TargetPid);
        Assert.True(tel.HighFrequency);

        var t0 = DateTime.UtcNow;
        for (int i = 0; i < 60; i++)
            tel.Emit(new TelemetrySample { TimestampUtc = t0.AddSeconds(i), GpuLoad = 99, CpuLoad = 30, CpuMaxCoreLoad = 50, Fps = 60, FrameCount = 60 },
                     Enumerable.Repeat(16.6f, 60).ToArray());
        await rec.StopAsync();

        var loaded = await store.GetSessionAsync(s.Id);
        Assert.NotNull(loaded);
        Assert.Equal(SessionStatus.Completed, loaded!.Status);
        Assert.Equal(60, (await store.LoadTelemetryAsync(s.Id)).Count);
        Assert.Equal(3600, (await store.LoadFramesAsync(s.Id)).Sum(f => f.FrameTimesMs.Length));
        Assert.NotNull(loaded.Summary);
        Assert.InRange(loaded.Summary!.AvgFps!.Value, 60, 61);
        Assert.Equal("Видеокарта", loaded.Report!.PrimaryLimiter);
        Assert.False(tel.HighFrequency);
    }

    [Fact]
    public async Task UnfinishedSession_IsRecoveredAsInterrupted()
    {
        var store = Store();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var id = await store.CreateSessionAsync(new GameSession { GameName = "Crashed", StartUtc = start, Status = SessionStatus.Recording, Origin = SessionOrigin.AutoDetected });
        await store.AppendTelemetryAsync(id, Enumerable.Range(0, 30)
            .Select(i => new TelemetrySample { TimestampUtc = start.AddSeconds(i), GpuLoad = 99, CpuLoad = 20 }).ToList());

        var rec = Recorder(new FakeTelemetry(), new FakeDetector(), store);
        await rec.InitializeAsync();

        var s = await store.GetSessionAsync(id);
        Assert.Equal(SessionStatus.Interrupted, s!.Status);
        Assert.Equal(start.AddSeconds(29), s.EndUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.NotNull(s.Report);
        Assert.Empty(await store.GetUnfinishedAsync());
    }

    [Fact]
    public async Task AutoDetectedGame_StartsAndStopsSession_ShortOnesAreDiscarded()
    {
        var store = Store();
        var tel = new FakeTelemetry();
        var det = new FakeDetector();
        var rec = Recorder(tel, det, store, new AppSettings { DetectionMode = DetectionMode.Auto, MinAutoSessionSeconds = 60 });
        await rec.InitializeAsync();

        var started = new TaskCompletionSource();
        var discarded = new TaskCompletionSource();
        rec.SessionStarted += (_, _) => started.TrySetResult();
        rec.SessionDiscarded += (_, _) => discarded.TrySetResult();

        var game = new DetectedGame(42, "Short Game", "short.exe", null, "test");
        det.Start(game);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tel.Emit(new TelemetrySample { TimestampUtc = DateTime.UtcNow, GpuLoad = 50 }, Array.Empty<float>());
        det.Exit(game);
        await discarded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(await store.ListSessionsAsync());
    }

    [Fact]
    public async Task Retention_PurgesRawDataButKeepsReports()
    {
        var store = Store();
        var old = DateTime.UtcNow.AddDays(-100);
        var id = await store.CreateSessionAsync(new GameSession { GameName = "Old", StartUtc = old, EndUtc = old.AddMinutes(10), Status = SessionStatus.Completed });
        await store.AppendTelemetryAsync(id, new[] { new TelemetrySample { TimestampUtc = old } });
        var r = await store.RunRetentionAsync(new AppSettings { RawTelemetryRetentionDays = 30, HistoryRetentionDays = 0, MaxDatabaseMb = 0 });

        Assert.Equal(1, r.RawDataPurged);
        Assert.NotNull(await store.GetSessionAsync(id));
        Assert.Empty(await store.LoadTelemetryAsync(id));
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeTelemetry : ITelemetryService
    {
        public event EventHandler<TelemetryTickEventArgs>? Tick;
        public TelemetrySample? Latest { get; private set; }
        public IReadOnlyList<SourceStatus> Sources { get; } = Array.Empty<SourceStatus>();
        public bool IsElevated => false;
        public int? TargetPid { get; private set; }
        public bool HighFrequency { get; set; }
        public IFrameTimeProvider FrameProvider => throw new NotSupportedException();
        public void SetTarget(int? pid, string? exeName) => TargetPid = pid;
        public void Start() { }
        public void Emit(TelemetrySample s, float[] frames) { Latest = s; Tick?.Invoke(this, new TelemetryTickEventArgs { Sample = s, FrameTimes = frames }); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDetector : IGameDetector
    {
        public event EventHandler<DetectedGame>? GameStarted;
        public event EventHandler<DetectedGame>? GameExited;
        public DetectedGame? Current { get; private set; }
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<RunningApp> GetRunningApps() => Array.Empty<RunningApp>();
        public void Start(DetectedGame g) { Current = g; GameStarted?.Invoke(this, g); }
        public void Exit(DetectedGame g) { Current = null; GameExited?.Invoke(this, g); }
    }

    private sealed class FakeSystem : ISystemInfoProvider
    {
        public Task<SystemInfo> GetAsync(bool refresh = false) => Task.FromResult(new SystemInfo { Cpu = new CpuInfo { Name = "CPU" } });
    }

    private sealed class FakeSettings(AppSettings s) : ISettingsService
    {
        public AppSettings Current { get; } = s;
        public event EventHandler? Changed { add { } remove { } }
        public void Save() { }
    }
}
