namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Global file-share / log monitoring feature configuration (opt-in). The actual watched directories
/// and log scans are configured per server in <see cref="ServerFileMonitoringConfig"/>.
/// </summary>
public class FileMonitoringFeatureConfig
{
    /// <summary>Whether the feature is enabled globally (default: false - opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Optional: custom check interval in seconds (null = use global interval).</summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>Default thresholds for file/log monitoring.</summary>
    public FileMonitoringDefaults Defaults { get; set; } = new();
}

/// <summary>Default thresholds applied when a watched directory does not override them.</summary>
public class FileMonitoringDefaults
{
    /// <summary>File age in minutes at/above which a Warning is raised (default 60).</summary>
    public int StuckFileAgeMinutesWarning { get; set; } = 60;

    /// <summary>File age in minutes at/above which a Critical is raised (default 240).</summary>
    public int StuckFileAgeMinutesCritical { get; set; } = 240;

    /// <summary>Cap on the number of reported stuck files / log matches per cycle (keeps the payload small).</summary>
    public int MaxReportedItems { get; set; } = 50;
}
