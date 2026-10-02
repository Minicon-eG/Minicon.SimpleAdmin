namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Overall service status
/// </summary>
public enum ServiceStatus
{
    /// <summary>
    /// Service is operating normally with all metrics within acceptable ranges
    /// </summary>
    Healthy,

    /// <summary>
    /// Service is operational but one or more metrics have exceeded warning thresholds
    /// </summary>
    Degraded,

    /// <summary>
    /// Service has critical issues with one or more metrics exceeding critical thresholds
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Service status cannot be determined
    /// </summary>
    Unknown,

    /// <summary>
    /// Server is not reachable (DNS failure, timeout, connection refused)
    /// </summary>
    Unreachable,

    /// <summary>
    /// Service has problems but all are acknowledged (someone is working on it)
    /// </summary>
    Acknowledged
}
