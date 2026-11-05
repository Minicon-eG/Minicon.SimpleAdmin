using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Global Windows Scheduled Task monitoring feature configuration (opt-in). The watched tasks are
/// configured per server in <see cref="ServerScheduledTasksConfig"/>.
/// </summary>
public class ScheduledTaskFeatureConfig
{
    /// <summary>Whether the feature is enabled globally (default: false - opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Optional: custom check interval in seconds (null = use global interval).</summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>Default thresholds applied when a watch pattern does not override them.</summary>
    public ScheduledTaskDefaults Defaults { get => _defaults; set => _defaults = value ?? new(); }
    private ScheduledTaskDefaults _defaults = new();

    /// <summary>
    /// Central ignore rules for Task Scheduler events (Microsoft-Windows-TaskScheduler/Operational).
    /// Matching events do not count against the event thresholds. Evaluated by the worker and
    /// centrally by the WebUI / notifier, like <see cref="EventLogFeatureConfig.IgnoreRules"/>.
    /// </summary>
    public List<ScheduledTaskIgnoreRule> IgnoreRules { get => _ignoreRules; set => _ignoreRules = value ?? new(); }
    private List<ScheduledTaskIgnoreRule> _ignoreRules = new();
}

/// <summary>Default thresholds for Scheduled Task monitoring.</summary>
public class ScheduledTaskDefaults
{
    /// <summary>A running task exceeding this runtime raises a Warning (minutes, 0 = off, default 120).</summary>
    public int MaxRuntimeMinutes { get; set; } = 120;

    /// <summary>
    /// Tolerance before a missed run is reported: an enabled task whose NextRunTime lies more than
    /// this many minutes in the past (and that is not running) counts as missed (default 30).
    /// </summary>
    public int MissedRunGraceMinutes { get; set; } = 30;

    /// <summary>Additional LastTaskResult codes treated as success (0 and informational HRESULTs always are).</summary>
    public List<int> AllowedResultCodes { get => _allowedResultCodes; set => _allowedResultCodes = value ?? new(); }
    private List<int> _allowedResultCodes = new();

    /// <summary>Look-back window for Task Scheduler events in minutes (default 60).</summary>
    public int EventTimeWindowMinutes { get; set; } = 60;

    /// <summary>Number of (non-ignored) error events in the window that raises a Warning (default 1).</summary>
    public int EventWarningCount { get; set; } = 1;

    /// <summary>Number of (non-ignored) error events in the window that raises a Critical (default 5).</summary>
    public int EventCriticalCount { get; set; } = 5;

    /// <summary>Cap on reported events per cycle (keeps the HTTP status payload small, default 50).</summary>
    public int MaxReportedEvents { get; set; } = 50;
}

/// <summary>
/// One long-term ignore rule for Task Scheduler events. An event is ignored when ALL set criteria
/// match (task path, event IDs, message substring) AND the rule applies to the server AND it has not expired.
/// </summary>
public class ScheduledTaskIgnoreRule
{
    /// <summary>Task path to match, e.g. "\Minicon\Export" (wildcard * supported, case-insensitive). Empty = any task.</summary>
    public string? TaskPath { get; set; }

    /// <summary>Event IDs to match. Empty = any event ID.</summary>
    public List<int> EventIds { get => _eventIds; set => _eventIds = value ?? new(); }
    private List<int> _eventIds = new();

    /// <summary>Case-insensitive substring (or wildcard pattern) the event message must contain. Empty = any message.</summary>
    public string? MessageContains { get; set; }

    /// <summary>Server names this rule applies to (wildcard * supported). Empty = all servers.</summary>
    public List<string> Servers { get => _servers; set => _servers = value ?? new(); }
    private List<string> _servers = new();

    /// <summary>Why this rule exists (audit trail).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Who created the rule (Windows user from the WebUI), optional.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Optional expiry (UTC). After this instant the rule no longer suppresses events. Null = permanent.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Form-binding helper: comma-separated <see cref="EventIds"/>. Not serialized.</summary>
    [JsonIgnore]
    public string EventIdsText
    {
        get => string.Join(",", EventIds);
        set => EventIds = (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    /// <summary>Form-binding helper: comma-separated <see cref="Servers"/>. Not serialized.</summary>
    [JsonIgnore]
    public string ServersText
    {
        get => string.Join(", ", Servers);
        set => Servers = (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>Per-server Scheduled Task checks (<c>server.checks.scheduledTasks</c>).</summary>
public class ServerScheduledTasksConfig
{
    /// <summary>Whether Scheduled Task checks are enabled for this server (default: false).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Watch patterns selecting the tasks to monitor. The first pattern whose include matches decides finally:
    /// if its exclude also matches, the task is not watched (later patterns are not consulted).
    /// </summary>
    public List<ScheduledTaskWatchConfig> Tasks { get => _tasks; set => _tasks = value ?? new(); }
    private List<ScheduledTaskWatchConfig> _tasks = new();

    /// <summary>Whether Task Scheduler error events of the watched tasks are evaluated (default: true).</summary>
    public bool CheckEvents { get; set; } = true;
}

/// <summary>
/// Selects tasks by full path pattern (e.g. "\Minicon\*", "\Export*") and defines how they are monitored.
/// Tasks below "\Microsoft\" are only selected when the include pattern itself starts with "\Microsoft\".
/// </summary>
public class ScheduledTaskWatchConfig
{
    /// <summary>Include pattern on the full task path (folder + name), wildcard * and ?, case-insensitive.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Exclude patterns on the full task path. Exclude wins: a task matching the include AND an exclude of this
    /// pattern is not watched at all, even if a later pattern would match it.
    /// </summary>
    public List<string> ExcludePatterns { get => _excludePatterns; set => _excludePatterns = value ?? new(); }
    private List<string> _excludePatterns = new();

    /// <summary>
    /// Cross-server mode: <see cref="ScheduledTaskMode.SingleActive"/> (default) = the task may be enabled on
    /// exactly one server of its environment; <see cref="ScheduledTaskMode.AllServers"/> = may/must run everywhere.
    /// </summary>
    public ScheduledTaskMode Mode { get; set; } = ScheduledTaskMode.SingleActive;

    /// <summary>Overrides <see cref="ScheduledTaskDefaults.MaxRuntimeMinutes"/> (0 = off).</summary>
    public int? MaxRuntimeMinutes { get; set; }

    /// <summary>Overrides <see cref="ScheduledTaskDefaults.MissedRunGraceMinutes"/>.</summary>
    public int? MissedRunGraceMinutes { get; set; }

    /// <summary>Whether missed runs are reported for these tasks (default: true).</summary>
    public bool CheckMissedRun { get; set; } = true;

    /// <summary>Additional result codes treated as success (added to the global list).</summary>
    public List<int> AllowedResultCodes { get => _allowedResultCodes; set => _allowedResultCodes = value ?? new(); }
    private List<int> _allowedResultCodes = new();

    /// <summary>Severity of a failed last run (default: Critical).</summary>
    public bool FailureIsCritical { get; set; } = true;

    /// <summary>Optional wiki/runbook link shown with problems of these tasks.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional remediation steps shown with problems of these tasks.</summary>
    public List<string> RemediationSteps { get => _remediationSteps; set => _remediationSteps = value ?? new(); }
    private List<string> _remediationSteps = new();

    /// <summary>Form-binding helper: comma-separated <see cref="ExcludePatterns"/>. Not serialized.</summary>
    [JsonIgnore]
    public string ExcludePatternsText
    {
        get => string.Join(", ", ExcludePatterns);
        set => ExcludePatterns = (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>Form-binding helper: comma-separated <see cref="AllowedResultCodes"/> (decimal or 0x hex). Not serialized.</summary>
    [JsonIgnore]
    public string AllowedResultCodesText
    {
        get => string.Join(", ", AllowedResultCodes.Select(c => c is < 0 or > 255 ? $"0x{c:X}" : c.ToString()));
        set => AllowedResultCodes = (value ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseCode)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();
    }

    private static int? ParseCode(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return unchecked((int)hex);
        return int.TryParse(s, out var dec) ? dec : null;
    }
}

/// <summary>Cross-server activation mode of a scheduled task.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduledTaskMode>))]
public enum ScheduledTaskMode
{
    /// <summary>The task may be enabled on exactly one server of its environment (0 or &gt;1 = problem).</summary>
    SingleActive,

    /// <summary>The task may/must run on every server it exists on.</summary>
    AllServers
}
