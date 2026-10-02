namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// IIS Application Pool feature configuration
/// </summary>
public class AppPoolFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: false - opt-in)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional: Custom check interval in seconds (null = use global interval)
    /// </summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>
    /// Default threshold values for all AppPools (can be overridden per server/pool)
    /// </summary>
    public AppPoolDefaults Defaults { get; set; } = new();
}

/// <summary>
/// Default threshold values for AppPool monitoring
/// </summary>
public class AppPoolDefaults
{
    /// <summary>
    /// Memory usage warning threshold in MB (default: 500)
    /// </summary>
    public int MemoryWarningMB { get; set; } = 500;

    /// <summary>
    /// Memory usage critical threshold in MB (default: 1000)
    /// </summary>
    public int MemoryCriticalMB { get; set; } = 1000;

    /// <summary>
    /// Uptime warning threshold in hours - when a recycle might be needed (default: 168 = 7 days)
    /// </summary>
    public int UptimeWarningHours { get; set; } = 168;
}

/// <summary>
/// Server-specific AppPool checks configuration
/// </summary>
public class AppPoolChecksConfig
{
    /// <summary>
    /// Whether AppPool checks are enabled for this server (default: false)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Automatically discover and monitor all AppPools on this server (default: false)
    /// When true, all pools are checked except those in IgnoredPools
    /// When false, only pools explicitly listed in Pools are checked
    ///</summary>
    public bool DiscoverAll { get; set; } = false;

    /// <summary>
    /// List of AppPool names to ignore when DiscoverAll is true
    /// Supports wildcards: "Legacy*", "*Test*", "Dev_*"
    /// </summary>
    public List<string> IgnoredPools { get; set; } = new();

    /// <summary>
    /// List of specific AppPools to monitor (only used when DiscoverAll is false)
    /// Or to override settings for specific pools when DiscoverAll is true
    /// </summary>
    public List<AppPoolConfig> Pools { get; set; } = new();
}

/// <summary>
/// Configuration for a specific AppPool to monitor
/// </summary>
public class AppPoolConfig
{
    /// <summary>
    /// The IIS AppPool name (required)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name for UI (optional, defaults to Name)
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this specific pool is enabled for monitoring (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Memory warning threshold in MB (overrides global default)
    /// </summary>
    public int? MemoryWarningMB { get; set; }

    /// <summary>
    /// Memory critical threshold in MB (overrides global default)
    /// </summary>
    public int? MemoryCriticalMB { get; set; }

    /// <summary>
    /// Uptime warning threshold in hours (overrides global default)
    /// </summary>
    public int? UptimeWarningHours { get; set; }

    /// <summary>
    /// Gets the effective display name (DisplayName if set, otherwise Name)
    /// </summary>
    public string GetEffectiveDisplayName() => DisplayName ?? Name;
}
