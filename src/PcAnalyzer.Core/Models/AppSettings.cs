using PcAnalyzer.Core.Analysis;

namespace PcAnalyzer.Core.Models;

public sealed class AppSettings
{
    public DetectionMode DetectionMode { get; set; } = DetectionMode.Hybrid;

    /// <summary>Telemetry polling interval.</summary>
    public int SampleIntervalMs { get; set; } = 1000;
    /// <summary>Power-saving monitoring: slower polling and fewer process scans while no session is recorded.</summary>
    public bool PowerSavingMonitoring { get; set; }

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool Notifications { get; set; } = true;

    public AnalysisThresholds Thresholds { get; set; } = new();

    /// <summary>Delete whole sessions older than N days (0 = keep forever).</summary>
    public int HistoryRetentionDays { get; set; }
    /// <summary>Delete raw telemetry and frame times older than N days; summaries and reports are kept.</summary>
    public int RawTelemetryRetentionDays { get; set; } = 60;
    public int MaxDatabaseMb { get; set; } = 2048;

    public FrameSourceKind FrameSource { get; set; } = FrameSourceKind.PresentMon;
    /// <summary>Path to PresentMon console executable (downloaded by the user from the official Intel GitHub).</summary>
    public string? PresentMonPath { get; set; }

    public int ChartWindowMinutes { get; set; } = 3;
    public bool ChartSmoothing { get; set; } = true;

    /// <summary>Auto-detected sessions shorter than this are discarded.</summary>
    public int MinAutoSessionSeconds { get; set; } = 60;
    /// <summary>Executables the user marked as games (file name, e.g. "game.exe").</summary>
    public List<string> UserGames { get; set; } = new();
    /// <summary>Executables never treated as games.</summary>
    public List<string> ExcludedApps { get; set; } = new();

    public string? ExportFolder { get; set; }
}

public enum FrameSourceKind
{
    None,
    PresentMon
}
