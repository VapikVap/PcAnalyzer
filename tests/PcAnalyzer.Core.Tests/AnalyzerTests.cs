using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Tests;

public class AnalyzerTests
{
    private static readonly PerformanceAnalyzer Analyzer = new();

    [Fact]
    public void GpuBound_IsDetected_AndCpuIsNotBlamed()
    {
        var b = new SessionBuilder();
        b.Add(300, (_, s) => { s.GpuLoad = 98; s.CpuLoad = 35; s.CpuMaxCoreLoad = 55; }, _ => b.Steady(48, 0.15));
        var (sum, rep) = Analyzer.Analyze(b.Build());

        Assert.Equal("Видеокарта", rep.PrimaryLimiter);
        var f = Assert.Single(rep.Findings, x => x.Code == "gpu_bound");
        Assert.Equal(Severity.Major, f.Severity); // 48 FPS < 60 target
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.DoesNotContain(rep.Findings, x => x.Code.StartsWith("cpu_"));
        Assert.InRange(sum.AvgFps!.Value, 46, 50);
        Assert.Contains("видеокарт", rep.Narrative);
    }

    [Fact]
    public void SingleThreadCpuBottleneck_IsDetected_WhenTotalLoadLooksNormal()
    {
        var b = new SessionBuilder();
        b.Add(300, (i, s) => { s.GpuLoad = 55 + i % 10; s.CpuLoad = 38; s.CpuMaxCoreLoad = 97; },
              i => b.Steady(i % 7 == 0 ? 40 : 70, 0.1));
        var (_, rep) = Analyzer.Analyze(b.Build());

        Assert.Equal("Процессор", rep.PrimaryLimiter);
        var f = Assert.Single(rep.Findings, x => x.Code == "cpu_single_thread");
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.DoesNotContain(rep.Findings, x => x.Code == "gpu_bound");
    }

    [Fact]
    public void GpuThermalThrottling_RequiresHeatAndClockDrop()
    {
        var b = new SessionBuilder();
        b.Add(200, (_, s) => { s.GpuLoad = 99; s.GpuTempC = 72; s.GpuCoreClockMhz = 1900; }, _ => b.Steady(90));
        b.Add(200, (_, s) => { s.GpuLoad = 99; s.GpuTempC = 89; s.GpuCoreClockMhz = 1500; }, _ => b.Steady(72));
        var (_, rep) = Analyzer.Analyze(b.Build());

        var f = Assert.Single(rep.Findings, x => x.Code == "gpu_thermal");
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.True(f.Severity >= Severity.Major);
        Assert.NotEmpty(f.When);
    }

    [Fact]
    public void HighTemperatureWithoutClockDrop_IsNotCalledThrottling()
    {
        var b = new SessionBuilder();
        b.Add(300, (_, s) => { s.GpuLoad = 99; s.GpuTempC = 86; s.GpuCoreClockMhz = 1900; }, _ => b.Steady(90));
        var (_, rep) = Analyzer.Analyze(b.Build());

        var f = Assert.Single(rep.Findings, x => x.Code == "gpu_thermal");
        Assert.DoesNotContain("троттлинг", f.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VramShortage_IsConfirmedOnlyWhenSpikesCoincide()
    {
        var b = new SessionBuilder();
        b.Add(200, (_, s) => { s.VramUsedMb = 5000; }, _ => b.Steady(80, 0.05));
        b.Add(200, (_, s) => { s.VramUsedMb = 8000; }, i =>
        {
            var f = b.Steady(80, 0.05);
            if (i % 4 == 0) f[10] = 90; // stutter while VRAM is full
            return f;
        });
        var (_, rep) = Analyzer.Analyze(b.Build());

        var v = Assert.Single(rep.Findings, x => x.Code == "vram");
        Assert.Equal(Severity.Major, v.Severity);
        Assert.Equal(Confidence.High, v.Confidence);
        Assert.Contains(rep.Findings, x => x.Code == "stutter");
    }

    [Fact]
    public void FpsCap_IsRecognised_AndNotReportedAsBottleneck()
    {
        var b = new SessionBuilder { RefreshHz = 60 };
        b.Add(300, (_, s) => { s.GpuLoad = 45; s.CpuMaxCoreLoad = 50; }, _ => b.Steady(60, 0.01));
        var (_, rep) = Analyzer.Analyze(b.Build());

        var f = Assert.Single(rep.Findings, x => x.Code == "fps_cap");
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Equal("Лимит FPS / V-Sync", rep.PrimaryLimiter);
        Assert.DoesNotContain(rep.Findings, x => x.Code is "gpu_bound" or "cpu_bound" or "cpu_single_thread");
    }

    [Fact]
    public void BothNearLimit_IsReported_WhenGpuInGreyZoneAndCpuSaturated()
    {
        // Like the real Once Human session on i7-3770K: CPU ~88 %, GPU 85–95 %.
        var b = new SessionBuilder();
        b.Add(300, (i, s) => { s.CpuLoad = 86 + i % 5; s.CpuMaxCoreLoad = 93; s.GpuLoad = 86 + i % 8; s.GameCpuPercent = 53; },
              _ => b.Steady(55, 0.2));
        var (_, rep) = Analyzer.Analyze(b.Build());

        Assert.Equal("Процессор и видеокарта (оба близки к пределу)", rep.PrimaryLimiter);
        var f = Assert.Single(rep.Findings, x => x.Code == "cpu_saturated");
        Assert.Equal(Severity.Major, f.Severity);
        Assert.Contains("одновременно", rep.Narrative);
    }

    [Fact]
    public void MaxFps_IsNotInflatedByShortBurstChunks()
    {
        var b = new SessionBuilder();
        // Mostly ~55 FPS, but one sample receives only 5 very short frames (buffered output).
        b.Add(120, (_, _) => { }, i => i == 60 ? new[] { 3f, 3f, 3.5f, 4f, 3f } : b.Steady(55, 0.05));
        var (sum, _) = Analyzer.Analyze(b.Build());
        Assert.InRange(sum.MaxFps!.Value, 50, 65);
    }

    [Fact]
    public void TooShortSession_SaysInsufficientData()
    {
        var b = new SessionBuilder();
        b.Add(5, (_, _) => { });
        var (_, rep) = Analyzer.Analyze(b.Build());
        Assert.Equal("Недостаточно данных, чтобы точно определить причину", rep.Headline);
        Assert.Empty(rep.Findings);
    }

    [Fact]
    public void MissingFrameSource_IsDisclosed_AndNoSmoothnessClaims()
    {
        var b = new SessionBuilder();
        b.Add(120, (_, s) => { s.GpuLoad = 99; });
        var (sum, rep) = Analyzer.Analyze(b.Build());

        Assert.Null(sum.AvgFps);
        Assert.Null(sum.SmoothnessScore);
        Assert.Contains(rep.DataNotes, n => n.Contains("FPS"));
        Assert.DoesNotContain(rep.Findings, f => f.Code == "stutter");
    }

    [Fact]
    public void MissingCpuTemperature_IsDisclosed_NotAssumedNormal()
    {
        var b = new SessionBuilder();
        b.Add(120, (_, s) => s.CpuTempC = null, _ => b.Steady(60));
        var (_, rep) = Analyzer.Analyze(b.Build());
        Assert.Contains(rep.DataNotes, n => n.Contains("Температура процессора"));
        Assert.DoesNotContain(rep.Findings, f => f.Code == "cpu_thermal");
    }

    [Fact]
    public void PowerSaverPlan_IsReported()
    {
        var b = new SessionBuilder();
        b.Add(60, (_, _) => { }, _ => b.Steady(60));
        var input = b.Build();
        input.Hardware!.PowerPlanGuid = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        var (_, rep) = Analyzer.Analyze(input);
        Assert.Contains(rep.Findings, f => f.Code == "power_saver_plan" && f.Confidence == Confidence.High);
    }

    [Fact]
    public void DropEvents_AreLocatedAndExplained()
    {
        var b = new SessionBuilder();
        b.Add(120, (_, s) => { s.GpuLoad = 90; }, _ => b.Steady(100));
        b.Add(5, (_, s) => { s.GpuLoad = 50; s.CpuMaxCoreLoad = 99; }, _ => b.Steady(40));
        b.Add(60, (_, s) => { s.GpuLoad = 90; }, _ => b.Steady(100));
        var (_, rep) = Analyzer.Analyze(b.Build());

        var e = Assert.Single(rep.Events, x => x.Kind == "Просадка FPS");
        Assert.InRange(e.OffsetSec, 119, 121);
        Assert.Contains(e.LikelyCauses, c => c.StartsWith("Процессор"));
    }
}
