namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Metric status enumeration for state-level metrics (AppPool memory, etc.)
/// </summary>
public enum MetricStatus
{
    /// <summary>
    /// Metric is within normal range
    /// </summary>
    Healthy,

    /// <summary>
    /// Metric exceeds warning threshold
    /// </summary>
    Warning,

    /// <summary>
    /// Metric exceeds critical threshold
    /// </summary>
    Critical,

    /// <summary>
    /// Status is unknown
    /// </summary>
    Unknown
}

/// <summary>
/// Overall status enumeration
/// </summary>
public enum OverallStatus
{
    /// <summary>
    /// Status is unknown (not yet checked)
    /// </summary>
    Unknown,

    /// <summary>
    /// All checks passed
    /// </summary>
    Healthy,

    /// <summary>
    /// Warning threshold exceeded
    /// </summary>
    Warning,

    /// <summary>
    /// Critical threshold exceeded or service down
    /// </summary>
    Critical,

    /// <summary>
    /// All problems are acknowledged
    /// </summary>
    Acknowledged
}
