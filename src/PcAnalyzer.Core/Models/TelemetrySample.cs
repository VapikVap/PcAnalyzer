namespace PcAnalyzer.Core.Models;

/// <summary>
/// One telemetry snapshot. Every metric is nullable: <c>null</c> means "the source is not
/// available on this machine", never "zero". Values are never invented.
/// </summary>
public sealed class TelemetrySample
{
    public DateTime TimestampUtc { get; set; }

    // ---- CPU ----
    public double? CpuLoad { get; set; }
    public double[]? CoreLoads { get; set; }
    public double? CpuMaxCoreLoad { get; set; }
    /// <summary>Effective clock (nominal frequency × % Processor Performance).</summary>
    public double? CpuClockMhz { get; set; }
    /// <summary>% Processor Performance relative to nominal frequency (can exceed 100 with boost).</summary>
    public double? CpuPerfPercent { get; set; }
    public double? CpuTempC { get; set; }
    public double? CpuPowerW { get; set; }

    // ---- GPU ----
    /// <summary>Overall GPU utilization (max of engine groups, Task-Manager style, or vendor sensor).</summary>
    public double? GpuLoad { get; set; }
    public double? Gpu3DLoad { get; set; }
    public double? GpuCopyLoad { get; set; }
    public double? GpuComputeLoad { get; set; }
    public double? GpuVideoLoad { get; set; }
    public double? GpuTempC { get; set; }
    public double? GpuHotSpotC { get; set; }
    public double? GpuCoreClockMhz { get; set; }
    public double? GpuMemClockMhz { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuFanRpm { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }

    // ---- Memory ----
    public double? RamUsedMb { get; set; }
    public double? RamTotalMb { get; set; }
    public double? CommitPercent { get; set; }
    public double? PageInputsPerSec { get; set; }
    public double? PagefilePercent { get; set; }

    // ---- Disk / Network ----
    /// <summary>Active time of the busiest physical disk, %.</summary>
    public double? DiskActivePercent { get; set; }
    public double? DiskReadBps { get; set; }
    public double? DiskWriteBps { get; set; }
    public double? DiskQueue { get; set; }
    public double? NetRxBps { get; set; }
    public double? NetTxBps { get; set; }

    // ---- Target (game) process ----
    /// <summary>CPU used by the game, % of the whole CPU (0–100).</summary>
    public double? GameCpuPercent { get; set; }
    public double? GameRamMb { get; set; }
    public double? GameGpuPercent { get; set; }
    public double? GameVramMb { get; set; }

    // ---- Frames (only when a frame-time source is attached) ----
    public double? Fps { get; set; }
    public double? FrameTimeAvgMs { get; set; }
    public double? FrameTimeMaxMs { get; set; }
    public int FrameCount { get; set; }

    /// <summary>Top resource consumers (filled every few seconds, not every sample).</summary>
    public List<ProcessUsage>? TopProcesses { get; set; }

    public double? RamPercent => RamUsedMb is { } u && RamTotalMb is > 0 ? u / RamTotalMb.Value * 100 : null;
    public double? VramPercent => VramUsedMb is { } u && VramTotalMb is > 0 ? u / VramTotalMb.Value * 100 : null;
}

public sealed class ProcessUsage
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    /// <summary>% of the whole CPU (0–100).</summary>
    public double CpuPercent { get; set; }
    public double RamMb { get; set; }
}

/// <summary>Frame times (ms) captured during one sample interval.</summary>
public sealed record FrameChunk(DateTime TimestampUtc, float[] FrameTimesMs);
