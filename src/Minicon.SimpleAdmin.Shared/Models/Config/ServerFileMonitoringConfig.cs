namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Per-server file-share / log monitoring configuration. Typically used by a central worker that
/// reaches file shares over UNC paths (e.g. <c>\\srv\d$\Daten\schnittstelle\kasse</c>).
/// </summary>
public class ServerFileMonitoringConfig
{
    /// <summary>Whether file monitoring is enabled for this server (default: false).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional business-hours window in the <see cref="TimeProfile"/> schedule syntax
    /// (e.g. "Mon-Fri 06:00-22:00"). When set, checks only run while the window is active.
    /// </summary>
    public string? Schedule { get; set; }

    /// <summary>Directories watched for stuck files (e.g. Eingang / Kasse / Log).</summary>
    public List<WatchedDirectory> Directories { get; set; } = new();

    /// <summary>Log files / globs scanned for error signatures.</summary>
    public List<LogScanConfig> LogScans { get; set; } = new();
}

/// <summary>A single directory watched for files that stay too long ("stuck files").</summary>
public class WatchedDirectory
{
    /// <summary>Display label (e.g. "Eingang"). Used in messages and the dashboard.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Directory path. May be a UNC path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Glob filters for files to include (e.g. "*.xml"). Empty = all files.</summary>
    public List<string> IncludePatterns { get; set; } = new();

    /// <summary>Glob filters for files to exclude (e.g. "*.tmp").</summary>
    public List<string> ExcludePatterns { get; set; } = new();

    /// <summary>Whether to recurse into sub-directories (default false).</summary>
    public bool Recursive { get; set; } = false;

    /// <summary>Override of the Warning age threshold in minutes (null = feature default).</summary>
    public int? AgeMinutesWarning { get; set; }

    /// <summary>Override of the Critical age threshold in minutes (null = feature default).</summary>
    public int? AgeMinutesCritical { get; set; }

    /// <summary>
    /// Optional daily cutoff time ("HH:mm", e.g. "21:45"). After this time any remaining file in the
    /// directory is reported (the directory is expected to be empty by then).
    /// </summary>
    public string? CutoffTime { get; set; }

    /// <summary>Severity for a cutoff violation: "Critical" (default) or "Warning".</summary>
    public string CutoffSeverity { get; set; } = "Critical";

    /// <summary>Optional wiki / remediation link shown on findings from this directory.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional step-by-step remediation hints shown on findings from this directory.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>A log file (or glob) scanned for one or more error signatures.</summary>
public class LogScanConfig
{
    /// <summary>Display label (e.g. "Schnittstellen-Log").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path of a single log file or a glob (e.g. "...\log\*.log"). May be a UNC path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>When the path is a glob, only scan files modified within this many minutes (null = all).</summary>
    public int? MaxFileAgeMinutes { get; set; }

    /// <summary>Maximum number of lines read from the tail of each file (0 = whole file, default 5000).</summary>
    public int MaxLines { get; set; } = 5000;

    /// <summary>Error signatures to search for.</summary>
    public List<LogPatternConfig> Patterns { get; set; } = new();

    /// <summary>Default wiki link for matches that do not set their own.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Default remediation hints for matches that do not set their own.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>A single regex error signature within a log scan.</summary>
public class LogPatternConfig
{
    /// <summary>Display name of the signature (e.g. "Hängengebliebene Nachricht").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>.NET regular expression matched against each log line (case-insensitive).</summary>
    public string Regex { get; set; } = string.Empty;

    /// <summary>Severity when the pattern matches: "Critical" (default) or "Warning".</summary>
    public string Severity { get; set; } = "Critical";

    /// <summary>Optional wiki link (overrides the log scan default).</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional remediation hints (overrides the log scan default).</summary>
    public List<string> RemediationSteps { get; set; } = new();
}
