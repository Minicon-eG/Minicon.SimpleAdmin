namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Metric status
/// </summary>
public enum MetricStatus
{
    /// <summary>
    /// Metric is within acceptable ranges
    /// </summary>
    Ok,

    /// <summary>
    /// Metric has exceeded the warning threshold
    /// </summary>
    Warning,

    /// <summary>
    /// Metric has exceeded the critical threshold
    /// </summary>
    Critical
}
