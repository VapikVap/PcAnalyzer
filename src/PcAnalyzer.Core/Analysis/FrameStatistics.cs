namespace PcAnalyzer.Core.Analysis;

public static class FrameStatistics
{
    /// <summary>Minimum number of frames for a meaningful 0.1% low.</summary>
    public const int MinFramesFor01Low = 5000;
    public const int MinFramesFor1Low = 300;

    public sealed record Result(
        int Count,
        double TotalMs,
        double AvgFps,
        double? P1LowFps,
        double? P01LowFps,
        double AvgMs,
        double MedianMs,
        double P99Ms,
        double MaxMs);

    /// <summary>
    /// Average FPS = frames / total time. "1% low" is reported as 1000 / 99th-percentile frame time
    /// (the percentile definition used by CapFrameX/PresentMon-based tools).
    /// </summary>
    public static Result? Compute(IReadOnlyCollection<float> frameTimesMs)
    {
        var valid = frameTimesMs.Where(f => f > 0 && f < 10_000).ToArray();
        if (valid.Length < 2) return null;

        double total = 0;
        foreach (var f in valid) total += f;

        var sorted = valid.ToArray();
        Array.Sort(sorted);
        double median = Percentile(sorted, 50);
        double p99 = Percentile(sorted, 99);
        double p999 = Percentile(sorted, 99.9);

        return new Result(
            Count: valid.Length,
            TotalMs: total,
            AvgFps: valid.Length / (total / 1000.0),
            P1LowFps: valid.Length >= MinFramesFor1Low ? 1000.0 / p99 : null,
            P01LowFps: valid.Length >= MinFramesFor01Low ? 1000.0 / p999 : null,
            AvgMs: total / valid.Length,
            MedianMs: median,
            P99Ms: p99,
            MaxMs: sorted[^1]);
    }

    /// <summary>
    /// FPS over consecutive ~1-second windows of frame time (not of wall-clock arrival time, which is bursty
    /// because PresentMon output is buffered). Used for min/max FPS so a handful of short frames can't fake a peak.
    /// </summary>
    public static List<double> WindowedFps(IEnumerable<float> frameTimesMs, double windowMs = 1000)
    {
        var result = new List<double>();
        double sum = 0;
        int n = 0;
        foreach (var f in frameTimesMs)
        {
            if (f <= 0 || f >= 10_000) continue;
            sum += f;
            n++;
            if (sum >= windowMs)
            {
                result.Add(n * 1000.0 / sum);
                sum = 0;
                n = 0;
            }
        }
        return result;
    }

    /// <summary>Linear-interpolated percentile over a sorted array.</summary>
    public static double Percentile(float[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var arr = values.Select(v => (float)v).ToArray();
        Array.Sort(arr);
        return Percentile(arr, p);
    }
}
