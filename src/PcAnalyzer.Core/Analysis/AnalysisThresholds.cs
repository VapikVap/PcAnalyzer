namespace PcAnalyzer.Core.Analysis;

/// <summary>User-tunable thresholds used by the diagnostic rules.</summary>
public sealed class AnalysisThresholds
{
    public double CpuTempWarnC { get; set; } = 90;
    public double GpuTempWarnC { get; set; } = 83;

    /// <summary>GPU load at/above which a sample is considered GPU-bound.</summary>
    public double GpuBoundLoad { get; set; } = 95;
    /// <summary>GPU load below which the GPU is considered "waiting".</summary>
    public double GpuNotBoundLoad { get; set; } = 85;
    public double CpuBoundCoreLoad { get; set; } = 90;
    public double CpuBoundTotalLoad { get; set; } = 85;

    /// <summary>FPS drop (in %) relative to the rolling median that counts as a drop event.</summary>
    public double FpsDropPercent { get; set; } = 25;
    public double StutterMultiplier { get; set; } = 2.5;
    public double StutterMinDeltaMs { get; set; } = 8;
    public double FreezeMs { get; set; } = 150;

    public double VramFullPercent { get; set; } = 95;
    public double RamFullPercent { get; set; } = 90;
    public double DiskBusyPercent { get; set; } = 90;
    /// <summary>Background process average CPU (% of whole CPU) worth reporting.</summary>
    public double BackgroundCpuPercent { get; set; } = 5;

    /// <summary>FPS considered comfortable when the monitor refresh rate is unknown.</summary>
    public double TargetFps { get; set; } = 60;
}
