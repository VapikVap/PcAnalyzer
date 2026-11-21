using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Tests;

/// <summary>Builds synthetic but realistic sessions for rule tests.</summary>
internal sealed class SessionBuilder
{
    private readonly List<TelemetrySample> _samples = new();
    private readonly List<FrameChunk> _frames = new();
    private readonly DateTime _start = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
    private readonly Random _rnd = new(42);
    public int RefreshHz { get; set; } = 144;

    /// <param name="frameMs">Frame time generator per second index (null = no frame source).</param>
    public SessionBuilder Add(int seconds, Action<int, TelemetrySample> fill, Func<int, float[]>? frames = null)
    {
        int offset = _samples.Count;
        for (int i = 0; i < seconds; i++)
        {
            int idx = offset + i;
            var t = _start.AddSeconds(idx);
            var s = new TelemetrySample
            {
                TimestampUtc = t,
                CpuLoad = 40, CpuMaxCoreLoad = 60, CoreLoads = new double[] { 40, 60, 30, 35 },
                CpuClockMhz = 4200, CpuPerfPercent = 120, CpuTempC = 65,
                GpuLoad = 70, GpuTempC = 65, GpuCoreClockMhz = 1900, VramUsedMb = 3000, VramTotalMb = 8192,
                RamUsedMb = 9000, RamTotalMb = 16384, DiskActivePercent = 5, PageInputsPerSec = 0,
            };
            fill(idx, s);
            if (frames != null)
            {
                var f = frames(idx);
                _frames.Add(new FrameChunk(t, f));
                s.FrameCount = f.Length;
                s.Fps = f.Length * 1000.0 / f.Sum(x => (double)x);
                s.FrameTimeAvgMs = f.Average(x => (double)x);
                s.FrameTimeMaxMs = f.Max();
            }
            _samples.Add(s);
        }
        return this;
    }

    public float[] Steady(double fps, double jitter = 0.03)
    {
        double ms = 1000 / fps;
        int n = (int)Math.Round(fps);
        return Enumerable.Range(0, n).Select(_ => (float)(ms * (1 + (_rnd.NextDouble() - 0.5) * 2 * jitter))).ToArray();
    }

    public AnalysisInput Build(string name = "Test Game") => new()
    {
        Session = new GameSession { Id = 1, GameName = name, ExeName = "test.exe", StartUtc = _start, EndUtc = _start.AddSeconds(_samples.Count) },
        Samples = _samples,
        Frames = _frames,
        Hardware = new SystemInfo
        {
            Cpu = new CpuInfo { Name = "Test CPU" },
            Gpus = { new GpuInfo { Name = "Test GPU", IsDiscrete = true } },
            Displays = { new DisplayInfo { Width = 1920, Height = 1080, RefreshHz = RefreshHz, IsPrimary = true } },
        },
    };
}
