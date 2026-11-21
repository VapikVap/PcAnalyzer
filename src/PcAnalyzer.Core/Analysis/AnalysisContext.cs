using System.Globalization;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Analysis;

public sealed class AnalysisInput
{
    public required GameSession Session { get; init; }
    public required IReadOnlyList<TelemetrySample> Samples { get; init; }
    public IReadOnlyList<FrameChunk> Frames { get; init; } = Array.Empty<FrameChunk>();
    public AnalysisThresholds Thresholds { get; init; } = new();
    public SystemInfo? Hardware { get; init; }
}

public enum LimiterKind
{
    Unknown,
    Gpu,
    Cpu,
    Capped,
    /// <summary>GPU in the 85–95 % grey zone while the CPU is also saturated: both are near the limit.</summary>
    Both
}

public sealed record FpsCapInfo(double Fps, double Share, bool MatchesRefreshRate);

public sealed record FrameSpike(int SampleIndex, double OffsetSec, float Ms);

/// <summary>
/// Pre-computed view of a session shared by all rules. Rules never look at a single
/// metric alone — the context gives them aligned series and per-sample classification.
/// </summary>
public sealed class AnalysisContext
{
    public GameSession Session { get; }
    public IReadOnlyList<TelemetrySample> Samples { get; }
    public IReadOnlyList<FrameChunk> Frames { get; }
    public AnalysisThresholds T { get; }
    public SystemInfo? Hardware { get; }

    public DateTime Start { get; }
    public double IntervalSec { get; }
    public double DurationSec { get; }

    public FrameStatistics.Result? FrameStats { get; }
    public List<FrameSpike> Spikes { get; } = new();
    public List<FrameSpike> Freezes { get; } = new();
    public bool[] SpikeAt { get; }
    public LimiterKind[] Limiter { get; }
    public FpsCapInfo? Cap { get; }
    public int? RefreshHz { get; }

    public bool HasFps { get; }
    public bool HasGpuLoad { get; }
    public bool HasCoreLoads { get; }
    public bool HasCpuTemp { get; }
    public bool HasGpuTemp { get; }
    public bool HasVram { get; }
    public bool HasCpuClock { get; }
    public bool HasGpuClock { get; }

    public AnalysisContext(AnalysisInput input)
    {
        Session = input.Session;
        Samples = input.Samples.OrderBy(s => s.TimestampUtc).ToList();
        Frames = input.Frames.OrderBy(f => f.TimestampUtc).ToList();
        T = input.Thresholds;
        Hardware = input.Hardware ?? input.Session.Hardware;
        RefreshHz = Hardware?.PrimaryDisplay?.RefreshHz is > 0 and var hz ? hz : null;

        Start = Samples.Count > 0 ? Samples[0].TimestampUtc : Session.StartUtc;
        IntervalSec = ComputeInterval(Samples);
        DurationSec = Samples.Count > 0 ? (Samples[^1].TimestampUtc - Start).TotalSeconds + IntervalSec : 0;

        HasFps = Samples.Count(s => s.Fps.HasValue) >= 5;
        HasGpuLoad = Samples.Any(s => s.GpuLoad.HasValue);
        HasCoreLoads = Samples.Any(s => s.CpuMaxCoreLoad.HasValue);
        HasCpuTemp = Samples.Any(s => s.CpuTempC.HasValue);
        HasGpuTemp = Samples.Any(s => s.GpuTempC.HasValue);
        HasVram = Samples.Any(s => s.VramPercent.HasValue);
        HasCpuClock = Samples.Any(s => s.CpuClockMhz.HasValue);
        HasGpuClock = Samples.Any(s => s.GpuCoreClockMhz.HasValue);

        SpikeAt = new bool[Samples.Count];
        var all = new List<float>();
        foreach (var f in Frames) all.AddRange(f.FrameTimesMs);
        FrameStats = FrameStatistics.Compute(all);
        if (FrameStats != null) DetectSpikes();

        Cap = DetectCap();
        Limiter = new LimiterKind[Samples.Count];
        for (int i = 0; i < Samples.Count; i++) Limiter[i] = Classify(Samples[i]);
    }

    // ---------------- helpers used by rules ----------------

    public double Offset(int sampleIndex) => (Samples[sampleIndex].TimestampUtc - Start).TotalSeconds;

    public static string Clock(double offsetSec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, offsetSec));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    /// <summary>Share of samples (where <paramref name="valid"/> holds) that satisfy <paramref name="cond"/>.</summary>
    public double Share(Func<TelemetrySample, bool> cond, Func<TelemetrySample, bool> valid)
    {
        int n = 0, hit = 0;
        foreach (var s in Samples)
        {
            if (!valid(s)) continue;
            n++;
            if (cond(s)) hit++;
        }
        return n == 0 ? 0 : (double)hit / n;
    }

    public double ShareIdx(Func<int, bool> cond, Func<int, bool> valid)
    {
        int n = 0, hit = 0;
        for (int i = 0; i < Samples.Count; i++)
        {
            if (!valid(i)) continue;
            n++;
            if (cond(i)) hit++;
        }
        return n == 0 ? 0 : (double)hit / n;
    }

    public double? Avg(Func<TelemetrySample, double?> sel, IEnumerable<int>? indices = null)
    {
        var vals = (indices ?? Enumerable.Range(0, Samples.Count)).Select(i => sel(Samples[i])).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return vals.Count == 0 ? null : vals.Average();
    }

    public double? Max(Func<TelemetrySample, double?> sel)
    {
        var vals = Samples.Select(sel).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return vals.Count == 0 ? null : vals.Max();
    }

    public double? Pct(Func<TelemetrySample, double?> sel, double p, IEnumerable<int>? indices = null)
    {
        var vals = (indices ?? Enumerable.Range(0, Samples.Count)).Select(i => sel(Samples[i])).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return vals.Count == 0 ? null : FrameStatistics.Percentile(vals, p);
    }

    public IEnumerable<int> Where(Func<int, bool> pred) => Enumerable.Range(0, Samples.Count).Where(pred);

    /// <summary>Merges matching samples into time ranges ("12:30–14:05"). Short blips are ignored.</summary>
    public List<string> Ranges(Func<int, bool> pred, int minSamples = 3, int maxRanges = 5)
    {
        var ranges = new List<(int a, int b)>();
        int gap = 2;
        int start = -1, last = -1;
        for (int i = 0; i < Samples.Count; i++)
        {
            if (!pred(i)) continue;
            if (start < 0) { start = last = i; continue; }
            if (i - last <= gap) { last = i; continue; }
            ranges.Add((start, last));
            start = last = i;
        }
        if (start >= 0) ranges.Add((start, last));
        var kept = ranges.Where(r => r.b - r.a + 1 >= minSamples).ToList();
        var result = kept.Take(maxRanges).Select(r => $"{Clock(Offset(r.a))}–{Clock(Offset(r.b) + IntervalSec)}").ToList();
        if (kept.Count > maxRanges) result.Add($"и ещё {kept.Count - maxRanges} интерв.");
        return result;
    }

    public static string F0(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) : "—";
    public static string F1(double? v) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    public static string P(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public bool IsCappedSample(TelemetrySample s) =>
        Cap != null && s.Fps is { } fps && Math.Abs(fps - Cap.Fps) <= CapBand(Cap.Fps);

    // ---------------- internals ----------------

    private static double ComputeInterval(IReadOnlyList<TelemetrySample> s)
    {
        if (s.Count < 2) return 1;
        var deltas = new List<double>();
        for (int i = 1; i < s.Count; i++) deltas.Add((s[i].TimestampUtc - s[i - 1].TimestampUtc).TotalSeconds);
        var med = FrameStatistics.Percentile(deltas, 50);
        return med is > 0.05 and < 60 ? med : 1;
    }

    private static double CapBand(double fps) => Math.Max(2, fps * 0.04);

    private void DetectSpikes()
    {
        var median = FrameStats!.MedianMs;
        double threshold = Math.Max(median * T.StutterMultiplier, median + T.StutterMinDeltaMs);
        var sampleTimes = Samples.Select(s => s.TimestampUtc).ToArray();
        foreach (var chunk in Frames)
        {
            int idx = NearestSample(sampleTimes, chunk.TimestampUtc);
            double off = (chunk.TimestampUtc - Start).TotalSeconds;
            foreach (var ms in chunk.FrameTimesMs)
            {
                if (ms >= T.FreezeMs)
                {
                    Freezes.Add(new FrameSpike(idx, off, ms));
                    Spikes.Add(new FrameSpike(idx, off, ms));
                    if (idx >= 0) SpikeAt[idx] = true;
                }
                else if (ms > threshold)
                {
                    Spikes.Add(new FrameSpike(idx, off, ms));
                    if (idx >= 0) SpikeAt[idx] = true;
                }
            }
        }
    }

    private static int NearestSample(DateTime[] times, DateTime t)
    {
        if (times.Length == 0) return -1;
        int i = Array.BinarySearch(times, t);
        if (i >= 0) return i;
        i = ~i;
        if (i == 0) return 0;
        if (i >= times.Length) return times.Length - 1;
        return (t - times[i - 1]) <= (times[i] - t) ? i - 1 : i;
    }

    private FpsCapInfo? DetectCap()
    {
        var fps = Samples.Where(s => s.Fps.HasValue).Select(s => s.Fps!.Value).ToList();
        if (fps.Count < 20) return null;
        double m = FrameStatistics.Percentile(fps, 50);
        if (m < 20) return null;
        double band = CapBand(m);
        double share = fps.Count(f => Math.Abs(f - m) <= band) / (double)fps.Count;
        if (share < 0.65) return null;

        // GPU- or CPU-bound games can also produce stable FPS; a cap means both had headroom.
        double? gpuMedian = Pct(s => s.GpuLoad, 50);
        if (gpuMedian is >= 90) return null;
        double? coreMedian = Pct(s => s.CpuMaxCoreLoad, 50);
        if (coreMedian >= T.CpuBoundCoreLoad) return null;

        bool refresh = RefreshHz is { } hz && Math.Abs(m - hz) <= 3;
        return new FpsCapInfo(m, share, refresh);
    }

    private LimiterKind Classify(TelemetrySample s)
    {
        if (s.GpuLoad is not { } gpu) return LimiterKind.Unknown;
        if (IsCappedSample(s)) return LimiterKind.Capped;
        if (gpu >= T.GpuBoundLoad) return LimiterKind.Gpu;
        bool cpuHigh = s.CpuMaxCoreLoad >= T.CpuBoundCoreLoad || s.CpuLoad >= T.CpuBoundTotalLoad ||
                       (s.GameCpuPercent.HasValue && s.CpuLoad.HasValue && s.GameCpuPercent >= T.CpuBoundTotalLoad);
        if (gpu < T.GpuNotBoundLoad && cpuHigh) return LimiterKind.Cpu;
        if (gpu >= T.GpuNotBoundLoad && (s.CpuLoad >= T.CpuBoundTotalLoad || s.CpuMaxCoreLoad >= 95)) return LimiterKind.Both;
        return LimiterKind.Unknown;
    }
}
