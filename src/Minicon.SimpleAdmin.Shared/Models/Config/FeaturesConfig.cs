namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Global feature toggles - controls which monitoring features are available
/// </summary>
public class FeaturesConfig
{
    /// <summary>
    /// System metrics feature (CPU, Memory, Disk) - always enabled by default
    /// </summary>
    public SystemMetricsFeatureConfig SystemMetrics { get; set; } = new();

    /// <summary>
    /// Windows Services monitoring feature
    /// </summary>
    public ServicesFeatureConfig Services { get; set; } = new();

    /// <summary>
    /// IIS Application Pools monitoring feature
    /// </summary>
    public AppPoolFeatureConfig AppPools { get; set; } = new();

    /// <summary>
    /// Windows Event Log monitoring feature
    /// </summary>
    public EventLogFeatureConfig EventLog { get; set; } = new();

    /// <summary>
    /// Certificate expiration monitoring feature
    /// </summary>
    public CertificateFeatureConfig Certificates { get; set; } = new();

    /// <summary>
    /// Problem acknowledgement feature
    /// </summary>
    public AcknowledgeFeatureConfig Acknowledge { get; set; } = new();

    /// <summary>
    /// SQL query checks feature (per-server configurable queries with result assertions)
    /// </summary>
    public SqlQueryFeatureConfig SqlQueries { get; set; } = new();

    /// <summary>
    /// BizTalk monitoring feature (applications, ports, receive locations, suspended instances)
    /// </summary>
    public BizTalkFeatureConfig BizTalk { get; set; } = new();

    /// <summary>
    /// File-share / log monitoring feature (stuck files + log error signatures)
    /// </summary>
    public FileMonitoringFeatureConfig FileMonitoring { get; set; } = new();

    /// <summary>
    /// Central notification feature (worker --notify mode): aggregates all servers and sends
    /// summary e-mails for unacknowledged problems with acknowledge deep-links.
    /// </summary>
    public NotificationsFeatureConfig Notifications { get; set; } = new();

}

/// <summary>
/// System metrics feature configuration (CPU, Memory, Disk)
/// </summary>
public class SystemMetricsFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Windows Services feature configuration
/// </summary>
public class ServicesFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;
}
