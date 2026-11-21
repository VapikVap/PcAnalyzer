namespace PcAnalyzer.Core.Models;

public enum SessionStatus
{
    Recording,
    Completed,
    Interrupted
}

/// <summary>How game sessions are started.</summary>
public enum DetectionMode
{
    /// <summary>Sessions start and stop only on game detection.</summary>
    Auto,
    /// <summary>Sessions start and stop only by the user.</summary>
    Manual,
    /// <summary>Automatic detection plus manual start/stop/rename.</summary>
    Hybrid
}

public enum SessionOrigin
{
    AutoDetected,
    Manual
}

public enum Severity
{
    None = 0,
    Minor = 1,
    Major = 2,
    Critical = 3
}

public enum Confidence
{
    Low,
    Medium,
    High
}
