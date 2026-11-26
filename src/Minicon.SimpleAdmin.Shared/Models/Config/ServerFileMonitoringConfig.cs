namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Per-server file-share / log monitoring configuration. Typically used by a central worker that
/// reaches file shares over UNC paths (e.g. <c>\\srv\d$\Daten\schnittstelle\kasse</c>).
/// </summary>
public class ServerFileMonitoringConfig : ConfigModel
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
public class WatchedDirectory : ConfigModel
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
public class LogScanConfig : ConfigModel
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

    /// <summary>
    /// Rules for <em>offene Läufe</em> — a start marker in the log without the matching end marker
    /// (e.g. "waiting for Result..." without a following "Verarbeitungsergebnis:").
    /// </summary>
    public List<LogPendingRule> PendingRules { get; set; } = new();

    /// <summary>
    /// Rules for <em>erwartete Läufe</em> — a run that should happen on certain days (e.g. every
    /// working day at 22:00) but is missing from the log.
    /// </summary>
    public List<ExpectedRunRule> ExpectedRuns { get; set; } = new();

    /// <summary>Default wiki link for matches that do not set their own.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Default remediation hints for matches that do not set their own.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>
/// A rule that detects runs which were started in the log but never finished (or were abandoned).
/// The classic case is an interface log that ends with "waiting for Result..." and no result line.
/// </summary>
public class LogPendingRule : ConfigModel
{
    /// <summary>Display name of the rule (e.g. "Lauf muss abgeschlossen sein").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Regular expression that marks the <em>start</em> of a run (e.g. "waiting for Result").</summary>
    public string StartRegex { get; set; } = string.Empty;

    /// <summary>Regular expression that marks the <em>successful end</em> of a run (e.g. "Verarbeitungsergebnis:").</summary>
    public string EndRegex { get; set; } = string.Empty;

    /// <summary>
    /// Optional regular expression that marks an <em>aborted</em> run (e.g. "abgebrochen|Abbruch").
    /// A matching abort marker ends the run silently: neither a pending nor an abandoned finding is reported.
    /// </summary>
    public string? AbortRegex { get; set; }

    /// <summary>
    /// A start without an end counts as "hanging" after this many minutes (default 30).
    /// A younger run is shown as "läuft noch" in the state but does not raise a problem.
    /// </summary>
    public int MaxMinutes { get; set; } = 30;

    /// <summary>Severity of the finding: "Critical" (default) or "Warning".</summary>
    public string Severity { get; set; } = "Critical";

    /// <summary>
    /// Whether a run that was superseded by a newer start (without an end in between) is reported as
    /// an "abgebrochener Lauf" (default true).
    /// </summary>
    public bool ReportAbandoned { get; set; } = true;

    /// <summary>
    /// Optional regular expression that extracts the timestamp from a line. The first capture group
    /// is used. When empty, the timestamp is taken from the line start with <see cref="TimestampFormat"/>.
    /// </summary>
    public string? TimestampRegex { get; set; }

    /// <summary>
    /// Format of the timestamp at the line start (default "yyyy-MM-dd HH:mm:ss.fff", local time of
    /// the server that writes the log).
    /// </summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Optional wiki link (overrides the log scan default).</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional remediation hints (overrides the log scan default).</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>
/// A rule that expects a run to happen on certain days (e.g. every working day at 22:00) and
/// reports it as missing when the log does not contain a matching line.
/// </summary>
public class ExpectedRunRule : ConfigModel
{
    /// <summary>Display name of the rule (e.g. "Abendlauf Kassen/Jukos").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Regular expression that proves the run happened — a line with a successful result
    /// (e.g. "Verarbeitungsergebnis:") or the start line of the run.
    /// </summary>
    public string MatchRegex { get; set; } = string.Empty;

    /// <summary>
    /// Days on which the run is expected, in the <see cref="TimeProfile"/> schedule syntax.
    /// Only the day part is used (e.g. "Mon-Fri" or "Mon,Wed,Fri"); an empty value means every day.
    /// </summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>
    /// Time of day the run is expected (e.g. "22:00"). The default is 22:00 — an empty value
    /// would silently skip the evaluation of the rule.
    /// </summary>
    public string ExpectedTime { get; set; } = "22:00";

    /// <summary>
    /// Grace period in minutes after <see cref="ExpectedTime"/> before the run counts as missing
    /// (default 120).
    /// </summary>
    public int GraceMinutes { get; set; } = 120;

    /// <summary>Severity of the finding: "Critical" (default) or "Warning".</summary>
    public string Severity { get; set; } = "Critical";

    /// <summary>
    /// Optional list of dates (ISO, e.g. "2026-12-24") on which the run is <em>not</em> expected
    /// (holidays). No automatic holiday calculation is done — the list is the single source.
    /// </summary>
    public List<string> Holidays { get; set; } = new();

    /// <summary>Optional wiki link (overrides the log scan default).</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional remediation hints (overrides the log scan default).</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>A single regex error signature within a log scan.</summary>
public class LogPatternConfig : ConfigModel
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
