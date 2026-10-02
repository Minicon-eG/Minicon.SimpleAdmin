namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Server configuration
/// </summary>
public class Server
{
    /// <summary>
    /// Gets or sets the unique server ID (defaults to Name if not set)
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the server name (hostname or cluster name)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name for UI (optional, defaults to Name)
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the server description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether this server is active for monitoring
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// Gets or sets the optional base URL override (default: http://localhost:PORT)
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the list of service types running on this server
    /// </summary>
    public List<ServiceType> ServiceTypes { get; set; } = new();

    /// <summary>
    /// Gets or sets the server-specific checks configuration
    /// </summary>
    public ServerChecksConfig? Checks { get; set; }

    /// <summary>
    /// Gets the effective ID (Id if set, otherwise Name)
    /// </summary>
    public string GetEffectiveId() => Id ?? Name;

    /// <summary>
    /// Gets the effective display name (DisplayName if set, otherwise Name)
    /// </summary>
    public string GetEffectiveDisplayName() => DisplayName ?? Name;
}

/// <summary>
/// Server-specific checks configuration
/// </summary>
public class ServerChecksConfig
{
    /// <summary>
    /// CPU threshold configuration
    /// </summary>
    public CpuCheckConfig? Cpu { get; set; }

    /// <summary>
    /// Memory threshold configuration
    /// </summary>
    public MemoryCheckConfig? Memory { get; set; }

    /// <summary>
    /// Disk checks configuration
    /// </summary>
    public List<DiskCheckConfig>? Disks { get; set; }

    /// <summary>
    /// Windows Services to monitor
    /// </summary>
    public ServerServicesConfig? Services { get; set; }

    /// <summary>
    /// AppPool checks configuration
    /// </summary>
    public AppPoolChecksConfig? AppPools { get; set; }

    /// <summary>
    /// Event Log checks configuration
    /// </summary>
    public EventLogChecksConfig? EventLog { get; set; }

    /// <summary>
    /// Certificate checks configuration
    /// </summary>
    public CertificateChecksConfig? Certificates { get; set; }

    /// <summary>
    /// BizTalk monitoring configuration (BizTalk Management API)
    /// </summary>
    public ServerBizTalkConfig? BizTalk { get; set; }

    /// <summary>
    /// SQL query checks configuration
    /// </summary>
    public ServerSqlQueriesConfig? SqlQueries { get; set; }

    /// <summary>
    /// Email probe sender configuration (sender role: sends probe emails via SMTP)
    /// </summary>
    public ServerEmailProbesConfig? EmailProbes { get; set; }

    /// <summary>
    /// Email delivery checker configuration (checker role: verifies probe emails via IMAP)
    /// </summary>
    public ServerEmailDeliveryConfig? EmailDelivery { get; set; }

    /// <summary>
    /// File-share / log monitoring configuration (stuck files + log error signatures)
    /// </summary>
    public ServerFileMonitoringConfig? FileMonitoring { get; set; }
}

/// <summary>
/// CPU check configuration
/// </summary>
public class CpuCheckConfig
{
    /// <summary>
    /// Warning threshold percentage (e.g., 70)
    /// </summary>
    public int WarningPercent { get; set; } = 70;

    /// <summary>
    /// Critical threshold percentage (e.g., 85)
    /// </summary>
    public int CriticalPercent { get; set; } = 85;
}

/// <summary>
/// Memory check configuration
/// </summary>
public class MemoryCheckConfig
{
    /// <summary>
    /// Warning threshold percentage (e.g., 80)
    /// </summary>
    public int WarningPercent { get; set; } = 80;

    /// <summary>
    /// Critical threshold percentage (e.g., 90)
    /// </summary>
    public int CriticalPercent { get; set; } = 90;
}

/// <summary>
/// Disk check configuration
/// </summary>
public class DiskCheckConfig
{
    /// <summary>
    /// Drive letter (e.g., "C:")
    /// </summary>
    public string Drive { get; set; } = string.Empty;

    /// <summary>
    /// Warning threshold percentage (e.g., 80)
    /// </summary>
    public int WarningPercent { get; set; } = 80;

    /// <summary>
    /// Critical threshold percentage (e.g., 90)
    /// </summary>
    public int CriticalPercent { get; set; } = 90;
}

/// <summary>
/// Per-server Windows Services monitoring container
/// </summary>
public class ServerServicesConfig
{
    /// <summary>
    /// Whether Windows Service monitoring is enabled for this server (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Windows services to monitor
    /// </summary>
    public List<ServiceCheckConfig> Checks { get; set; } = new();
}

/// <summary>
/// Service check configuration
/// </summary>
public class ServiceCheckConfig
{
    /// <summary>
    /// Windows service name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name for UI (optional)
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Monitor services configured as Automatic or Automatic (Delayed) start (default: true)
    /// </summary>
    public bool MonitorAutomatic { get; set; } = true;

    /// <summary>
    /// Monitor services configured as Manual start (default: false)
    /// </summary>
    public bool MonitorManual { get; set; } = false;

    /// <summary>
    /// Gets the effective display name
    /// </summary>
    public string GetEffectiveDisplayName() => DisplayName ?? Name;
}

