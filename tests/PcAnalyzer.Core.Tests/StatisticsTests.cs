using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Tests;

public class StatisticsTests
{
    [Fact]
    public void FrameStatistics_ComputesAverageAndPercentileLows()
    {
        // 990 frames at 10 ms + 10 frames at 50 ms
        var frames = Enumerable.Repeat(10f, 990).Concat(Enumerable.Repeat(50f, 10)).ToArray();
        var r = FrameStatistics.Compute(frames)!;

        Assert.Equal(1000, r.Count);
        Assert.Equal(1000 / (10_400 / 1000.0), r.AvgFps, 3); // 96.15 FPS
        Assert.InRange(r.P1LowFps!.Value, 19.9, 100);          // P99 lies at the 10↔50 ms boundary
        Assert.Null(r.P01LowFps);                               // needs ≥ 5000 frames
        Assert.Equal(50, r.MaxMs);
    }

    [Fact]
    public void FrameStatistics_ReturnsNullWithoutData() => Assert.Null(FrameStatistics.Compute(Array.Empty<float>()));

    [Fact]
    public void Comparer_DetectsImprovement_AndIgnoresNoise()
    {
        var a = new GameSession { GameName = "G", StartUtc = DateTime.UtcNow.AddHours(-2), EndUtc = DateTime.UtcNow.AddHours(-1.8), Summary = new SessionSummary { AvgFps = 60, P1LowFps = 40 } };
        var b = new GameSession { GameName = "G", StartUtc = DateTime.UtcNow.AddHours(-1), EndUtc = DateTime.UtcNow.AddHours(-0.8), Summary = new SessionSummary { AvgFps = 72, P1LowFps = 52 } };
        var r = SessionComparer.Compare(a, b);
        Assert.StartsWith("Стало лучше", r.Verdict);

        var c = new GameSession { GameName = "G", StartUtc = b.StartUtc, EndUtc = b.EndUtc, Summary = new SessionSummary { AvgFps = 61, P1LowFps = 40.5 } };
        Assert.StartsWith("Изменения не дали", SessionComparer.Compare(a, c).Verdict);
    }
}
