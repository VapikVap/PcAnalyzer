using PcAnalyzer.Core.Models;

namespace PcAnalyzer.Core.Analysis;

/// <summary>One diagnosed problem (or a notable fact) with the evidence behind it.</summary>
public sealed class Finding
{
    public string Code { get; set; } = "";
    public Severity Severity { get; set; }
    public Confidence Confidence { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What was detected, in plain language.</summary>
    public string Description { get; set; } = "";
    /// <summary>Time ranges relative to session start, e.g. "12:30–14:05".</summary>
    public List<string> When { get; set; } = new();
    /// <summary>Measured values that support the conclusion.</summary>
    public List<string> Evidence { get; set; } = new();
    public string Impact { get; set; } = "";
    public List<string> Recommendations { get; set; } = new();
    /// <summary>Things the finding is about that the UI can act on (e.g. process names for "background").</summary>
    public List<string> Subjects { get; set; } = new();
    /// <summary>Share of the session the condition was present (0–1), if meaningful.</summary>
    public double? Share { get; set; }
}

/// <summary>A moment where performance dropped, with the most likely causes at that time.</summary>
public sealed class PerformanceEvent
{
    public double OffsetSec { get; set; }
    public double DurationSec { get; set; }
    public string Kind { get; set; } = "";
    public double? FpsBefore { get; set; }
    public double? FpsDuring { get; set; }
    public double? WorstFrameMs { get; set; }
    public List<string> LikelyCauses { get; set; } = new();
}

public sealed class SessionReport
{
    public string Headline { get; set; } = "";
    public string? PrimaryLimiter { get; set; }
    public string Narrative { get; set; } = "";
    public List<Finding> Findings { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public List<PerformanceEvent> Events { get; set; } = new();
    /// <summary>Which data sources were missing and what that means for the conclusions.</summary>
    public List<string> DataNotes { get; set; } = new();
    public string SmoothnessLabel { get; set; } = "";
    public string GeneratedBy { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
}

public static class SeverityText
{
    public static string Icon(Severity s) => s switch
    {
        Severity.Critical => "🔴",
        Severity.Major => "🟠",
        Severity.Minor => "🟡",
        _ => "🟢"
    };

    public static string Name(Severity s) => s switch
    {
        Severity.Critical => "Критическая",
        Severity.Major => "Значительная",
        Severity.Minor => "Незначительная",
        _ => "Информация"
    };

    public static string Name(Confidence c) => c switch
    {
        Confidence.High => "высокая",
        Confidence.Medium => "средняя",
        _ => "низкая"
    };
}
