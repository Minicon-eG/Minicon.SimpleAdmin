namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Snapshot of file-share / log monitoring for one server. Stores only the problematic findings
/// (stuck files + matched log lines) plus small totals to keep the payload compact — this object is
/// serialized into the per-server status JSON that the central notifier pulls over HTTP.
/// </summary>
public class FileMonitoringState
{
    /// <summary>Number of watched directories that were enumerated this cycle.</summary>
    public int DirectoriesChecked { get; set; }

    /// <summary>Number of log files that were scanned this cycle.</summary>
    public int LogFilesScanned { get; set; }

    /// <summary>True when at least one watched path could not be reached (share offline, access denied, ...).</summary>
    public bool AnyError { get; set; }

    /// <summary>Detail of the first access/IO error encountered.</summary>
    public string? Error { get; set; }

    /// <summary>True when the configured business-hours schedule excluded this cycle (no checks ran).</summary>
    public bool OutsideBusinessHours { get; set; }

    /// <summary>Files considered "stuck" (too old and/or present after the daily cutoff).</summary>
    public List<StuckFile> StuckFiles { get; set; } = new();

    /// <summary>Aggregated log-pattern matches (one entry per pattern per log, with a hit count + sample).</summary>
    public List<LogMatch> LogMatches { get; set; } = new();
}

/// <summary>A file that has been sitting in a watched directory beyond its allowed age / cutoff.</summary>
public class StuckFile
{
    /// <summary>Label of the watched directory (e.g. "Eingang", "Kasse").</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Full path of the offending file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Last-write timestamp of the file (UTC).</summary>
    public DateTime LastModified { get; set; }

    /// <summary>Age of the file in minutes at detection time.</summary>
    public double AgeMinutes { get; set; }

    /// <summary>"age" (older than threshold) or "cutoff" (present after the daily cutoff time).</summary>
    public string Reason { get; set; } = "age";

    /// <summary>Whether this finding is Critical (otherwise Warning).</summary>
    public bool IsCritical { get; set; }

    /// <summary>Optional wiki / remediation link configured on the watched directory.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional step-by-step remediation hints configured on the watched directory.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>An aggregated set of log lines matching one configured error signature.</summary>
public class LogMatch
{
    /// <summary>Label of the log scan (e.g. "Schnittstellen-Log").</summary>
    public string LogScan { get; set; } = string.Empty;

    /// <summary>Path of the log file the match was found in.</summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>Name of the configured pattern/signature that matched.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Number of lines matching the pattern in this log.</summary>
    public int Count { get; set; }

    /// <summary>A sample matched line (truncated) for the message/detail view.</summary>
    public string SampleLine { get; set; } = string.Empty;

    /// <summary>Whether this finding is Critical (otherwise Warning).</summary>
    public bool IsCritical { get; set; }

    /// <summary>Optional wiki / remediation link configured on the pattern or log scan.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional step-by-step remediation hints configured on the pattern or log scan.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}
