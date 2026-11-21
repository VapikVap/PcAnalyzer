using PcAnalyzer.Core.Analysis;

namespace PcAnalyzer.Core.Models;

public sealed class GameSession
{
    public long Id { get; set; }
    public string GameName { get; set; } = "";
    public string? ExeName { get; set; }
    public string? ExePath { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public SessionStatus Status { get; set; }
    public SessionOrigin Origin { get; set; }
    /// <summary>User label, e.g. "До изменения настроек" / "После".</summary>
    public string? Label { get; set; }
    public string? Notes { get; set; }
    public SystemInfo? Hardware { get; set; }
    public SessionSummary? Summary { get; set; }
    public SessionReport? Report { get; set; }

    public TimeSpan Duration => (EndUtc ?? DateTime.UtcNow) - StartUtc;
}

/// <summary>Aggregated numbers of a session. Null = metric not available.</summary>
public sealed class SessionSummary
{
    public int SampleCount { get; set; }
    public double DurationSec { get; set; }

    public int FrameCount { get; set; }
    public double? AvgFps { get; set; }
    public double? P1LowFps { get; set; }
    public double? P01LowFps { get; set; }
    public double? MinFps { get; set; }
    public double? MaxFps { get; set; }
    public double? AvgFrameTimeMs { get; set; }
    public double? MaxFrameTimeMs { get; set; }
    public int StutterCount { get; set; }
    public int FreezeCount { get; set; }

    public double? AvgCpu { get; set; }
    public double? MaxCpu { get; set; }
    public double? AvgMaxCore { get; set; }
    public double[]? PerCoreAvg { get; set; }
    public double? AvgCpuClock { get; set; }
    public double? AvgCpuTemp { get; set; }
    public double? MaxCpuTemp { get; set; }

    public double? AvgGpu { get; set; }
    public double? MaxGpu { get; set; }
    public double? AvgGpuClock { get; set; }
    public double? AvgGpuTemp { get; set; }
    public double? MaxGpuTemp { get; set; }

    public double? AvgRamUsedMb { get; set; }
    public double? MaxRamUsedMb { get; set; }
    public double? RamTotalMb { get; set; }
    public double? AvgVramMb { get; set; }
    public double? MaxVramMb { get; set; }
    public double? VramTotalMb { get; set; }

    public double? SmoothnessScore { get; set; }
    public double? OverallScore { get; set; }
    public Severity WorstSeverity { get; set; }
    public int IssueCount { get; set; }
    public string? Diagnosis { get; set; }
}
