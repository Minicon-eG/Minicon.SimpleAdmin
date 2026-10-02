using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Runtime status with historical metrics
/// </summary>
public class RuntimeStatus
{
    /// <summary>
    /// Gets or sets the server name
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service type
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp of the last status update
    /// </summary>
    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Gets or sets the overall service status
    /// </summary>
    public ServiceStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the current metrics for this service
    /// </summary>
    public List<Metric> Metrics { get; set; } = new();

    /// <summary>
    /// Gets or sets the historical metric snapshots
    /// </summary>
    public List<MetricSnapshot> History { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of available endpoints
    /// </summary>
    public List<EndpointStatus> Endpoints { get; set; } = new();

    /// <summary>
    /// Gets or sets an optional status message (e.g., reason for Unreachable status)
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Gets or sets the AppPool states for this server (keyed by pool name)
    /// </summary>
    public Dictionary<string, AppPoolState>? AppPools { get; set; }

    /// <summary>
    /// Gets or sets the Windows Service states for this server (keyed by service name)
    /// </summary>
    public Dictionary<string, ServiceState>? Services { get; set; }

    /// <summary>
    /// Gets or sets the Event Log check results (null if check not enabled)
    /// </summary>
    public EventLogState? EventLogs { get; set; }

    /// <summary>
    /// Gets or sets the SQL query check results keyed by check group Id (null if not enabled)
    /// </summary>
    public Dictionary<string, SqlQueryCheckState>? SqlQueryChecks { get; set; }

    /// <summary>
    /// Gets or sets the email probe states keyed by probe name (sender role; null if not enabled)
    /// </summary>
    public Dictionary<string, EmailProbeState>? EmailProbes { get; set; }

    /// <summary>
    /// Gets or sets the email delivery check states keyed by check name (checker role; null if not enabled)
    /// </summary>
    public Dictionary<string, EmailDeliveryCheckState>? EmailDelivery { get; set; }

    /// <summary>
    /// Gets or sets the certificate states keyed by thumbprint (null if not enabled)
    /// </summary>
    public Dictionary<string, CertificateState>? Certificates { get; set; }

    /// <summary>
    /// Gets or sets the BizTalk monitoring state for this server (null if not enabled)
    /// </summary>
    public BizTalkState? BizTalk { get; set; }

    /// <summary>
    /// Gets or sets the file-share / log monitoring state for this server (null if not enabled)
    /// </summary>
    public FileMonitoringState? FileMonitoring { get; set; }
}
