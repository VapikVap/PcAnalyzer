using System.Globalization;
using System.Text;
using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Export;

public static class CsvExporter
{
    private static readonly (string Name, Func<TelemetrySample, double?> Get)[] Columns =
    {
        ("cpu_load_pct", s => s.CpuLoad), ("cpu_max_core_pct", s => s.CpuMaxCoreLoad), ("cpu_clock_mhz", s => s.CpuClockMhz),
        ("cpu_perf_pct", s => s.CpuPerfPercent), ("cpu_temp_c", s => s.CpuTempC), ("cpu_power_w", s => s.CpuPowerW),
        ("gpu_load_pct", s => s.GpuLoad), ("gpu_3d_pct", s => s.Gpu3DLoad), ("gpu_copy_pct", s => s.GpuCopyLoad),
        ("gpu_compute_pct", s => s.GpuComputeLoad), ("gpu_video_pct", s => s.GpuVideoLoad), ("gpu_temp_c", s => s.GpuTempC),
        ("gpu_hotspot_c", s => s.GpuHotSpotC), ("gpu_core_mhz", s => s.GpuCoreClockMhz), ("gpu_mem_mhz", s => s.GpuMemClockMhz),
        ("gpu_power_w", s => s.GpuPowerW), ("gpu_fan_rpm", s => s.GpuFanRpm), ("vram_used_mb", s => s.VramUsedMb), ("vram_total_mb", s => s.VramTotalMb),
        ("ram_used_mb", s => s.RamUsedMb), ("ram_total_mb", s => s.RamTotalMb), ("commit_pct", s => s.CommitPercent),
        ("page_inputs_per_s", s => s.PageInputsPerSec), ("pagefile_pct", s => s.PagefilePercent),
        ("disk_active_pct", s => s.DiskActivePercent), ("disk_read_bps", s => s.DiskReadBps), ("disk_write_bps", s => s.DiskWriteBps),
        ("disk_queue", s => s.DiskQueue), ("net_rx_bps", s => s.NetRxBps), ("net_tx_bps", s => s.NetTxBps),
        ("game_cpu_pct", s => s.GameCpuPercent), ("game_ram_mb", s => s.GameRamMb), ("game_gpu_pct", s => s.GameGpuPercent), ("game_vram_mb", s => s.GameVramMb),
        ("fps", s => s.Fps), ("frametime_avg_ms", s => s.FrameTimeAvgMs), ("frametime_max_ms", s => s.FrameTimeMaxMs),
    };

    /// <summary>Per-sample telemetry. Empty cell = metric not available (never zero-filled).</summary>
    public static void WriteTelemetry(string path, GameSession session, IReadOnlyList<TelemetrySample> samples)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("# " + session.GameName + " | " + session.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv));
        w.WriteLine("timestamp_utc,offset_s," + string.Join(",", Columns.Select(c => c.Name)) + ",core_loads");
        foreach (var s in samples)
        {
            var sb = new StringBuilder();
            sb.Append(s.TimestampUtc.ToString("o", inv)).Append(',');
            sb.Append((s.TimestampUtc - session.StartUtc).TotalSeconds.ToString("0.###", inv));
            foreach (var c in Columns)
            {
                sb.Append(',');
                if (c.Get(s) is { } v) sb.Append(v.ToString("0.###", inv));
            }
            sb.Append(',');
            if (s.CoreLoads != null) sb.Append('"').Append(string.Join(";", s.CoreLoads.Select(v => v.ToString("0.#", inv)))).Append('"');
            w.WriteLine(sb.ToString());
        }
    }

    /// <summary>Every captured frame time.</summary>
    public static void WriteFrames(string path, GameSession session, IReadOnlyList<FrameChunk> chunks)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("sample_offset_s,frame_time_ms");
        foreach (var c in chunks)
        {
            string off = (c.TimestampUtc - session.StartUtc).TotalSeconds.ToString("0.###", inv);
            foreach (var ms in c.FrameTimesMs) w.WriteLine(off + "," + ms.ToString("0.###", inv));
        }
    }
}
