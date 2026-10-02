namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// State of a single IIS Application Pool
/// </summary>
public class AppPoolState
{
    /// <summary>
    /// AppPool name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Current status (Running, Stopped, Starting, Stopping)
    /// </summary>
    public AppPoolStatus Status { get; set; } = AppPoolStatus.Unknown;

    /// <summary>
    /// Worker process ID (if running)
    /// </summary>
    public int? WorkerProcessId { get; set; }

    /// <summary>
    /// Current memory usage in MB
    /// </summary>
    public double MemoryMB { get; set; }

    /// <summary>
    /// Memory status based on thresholds
    /// </summary>
    public MetricStatus MemoryStatus { get; set; } = MetricStatus.Healthy;

    /// <summary>
    /// Memory warning threshold in MB (stored from config for use in problem messages)
    /// </summary>
    public int MemoryWarningMB { get; set; }

    /// <summary>
    /// Memory critical threshold in MB (stored from config for use in problem messages)
    /// </summary>
    public int MemoryCriticalMB { get; set; }

    /// <summary>
    /// Uptime in hours since last recycle/start
    /// </summary>
    public double UptimeHours { get; set; }

    /// <summary>
    /// Uptime status based on configured warning threshold
    /// </summary>
    public MetricStatus UptimeStatus { get; set; } = MetricStatus.Healthy;

    /// <summary>
    /// Uptime warning threshold in hours (stored from config for use in problem messages)
    /// </summary>
    public int UptimeWarningHours { get; set; }

    /// <summary>
    /// Timestamp of last recycle
    /// </summary>
    public DateTime? LastRecycle { get; set; }

    /// <summary>
    /// Timestamp when pool was stopped (if stopped)
    /// </summary>
    public DateTime? StoppedSince { get; set; }

    /// <summary>
    /// Reason for stop (if stopped)
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// Whether the pool is in a healthy state (running and within all thresholds)
    /// </summary>
    public bool IsHealthy => Status == AppPoolStatus.Running &&
                             MemoryStatus == MetricStatus.Healthy &&
                             UptimeStatus == MetricStatus.Healthy;

    /// <summary>
    /// Gets a summary status for the pool
    /// </summary>
    public OverallStatus GetOverallStatus()
    {
        if (Status != AppPoolStatus.Running)
            return OverallStatus.Critical;

        if (MemoryStatus == MetricStatus.Critical)
            return OverallStatus.Critical;
        if (MemoryStatus == MetricStatus.Warning || UptimeStatus == MetricStatus.Warning)
            return OverallStatus.Warning;
        return OverallStatus.Healthy;
    }
}

/// <summary>
/// AppPool status enumeration
/// </summary>
public enum AppPoolStatus
{
    /// <summary>
    /// Status is unknown
    /// </summary>
    Unknown,

    /// <summary>
    /// Pool is starting
    /// </summary>
    Starting,

    /// <summary>
    /// Pool is running
    /// </summary>
    Running,

    /// <summary>
    /// Pool is stopping
    /// </summary>
    Stopping,

    /// <summary>
    /// Pool is stopped
    /// </summary>
    Stopped
}
