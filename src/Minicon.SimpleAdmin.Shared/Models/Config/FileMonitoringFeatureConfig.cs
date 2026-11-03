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

    /// <summary>
    /// Relative size change in percent at/above which a Warning is raised for a watched file
    /// (default 50). 0 disables the size-change detection.
    /// </summary>
    public int SizeChangePercentWarning { get; set; } = 50;

    /// <summary>
    /// Relative size change in percent at/above which a Critical is raised for a watched file
    /// (default 200). 0 disables the critical rating.
    /// </summary>
    public int SizeChangePercentCritical { get; set; } = 200;

    /// <summary>Cap on the number of reported stuck files / log matches per cycle (keeps the payload small).</summary>
    public int MaxReportedItems { get; set; } = 50;

    /// <summary>
    /// Whether the offset tracking (stateful log de-duplication) is active: only the log lines written
    /// since the last cycle are reported. When switched off, the tail of the log is read in every
    /// cycle (old behaviour). Default: true.
    /// </summary>
    public bool OffsetTrackingEnabled { get; set; } = true;
}
