using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Snapshot of the watched Windows Scheduled Tasks of one server. The evaluation flags are set by the
/// worker's ScheduledTaskChecker so problem derivation is a pure mapping. Serialized into the per-server
/// status JSON that the central WebUI / notifier pull over HTTP — the cross-server single-active check
/// runs centrally on these snapshots.
/// </summary>
public class ScheduledTaskState
{
    /// <summary>When the tasks were queried (UTC).</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>True when the task query itself failed (PowerShell error, timeout, access denied).</summary>
    public bool QueryFailed { get; set; }

    /// <summary>Error detail when <see cref="QueryFailed"/> is set.</summary>
    public string? Error { get; set; }

    /// <summary>Watched tasks (after include/exclude filtering), sorted by path.</summary>
    public List<ScheduledTaskInfo> Tasks { get; set; } = new();

    /// <summary>Non-ignored Task Scheduler error events of the watched tasks in the look-back window (newest first, capped).</summary>
    public List<ScheduledTaskEvent> RecentEvents { get; set; } = new();

    /// <summary>Number of non-ignored error events in the window (may exceed <see cref="RecentEvents"/> count).</summary>
    public int EventCount { get; set; }

    /// <summary>Number of events suppressed by ignore rules.</summary>
    public int IgnoredEventCount { get; set; }

    /// <summary>Status of the event evaluation (Healthy/Warning/Critical against the event thresholds).</summary>
    public MetricStatus EventStatus { get; set; } = MetricStatus.Healthy;

    /// <summary>
    /// True when event checking is on but the Task Scheduler history (Microsoft-Windows-TaskScheduler/Operational)
    /// is disabled or missing: error events cannot be detected (see <see cref="EventLogDisabledReason"/>).
    /// </summary>
    public bool EventLogDisabled { get; set; }

    /// <summary>Error while reading the Task Scheduler events (null = read fine or event checking off).</summary>
    public string? EventError { get; set; }

    /// <summary>
    /// Number of watched tasks selected by each watch pattern (key = pattern as configured). A pattern with 0
    /// hits usually means a typo or a renamed/deleted task. Only filled when the query succeeded.
    /// </summary>
    public Dictionary<string, int> PatternMatches { get; set; } = new();

    /// <summary>
    /// Non-ignored error events aggregated per (task path, event ID), counted after the per-run de-duplication —
    /// unlike <see cref="RecentEvents"/> not capped per task (at most 200 groups, the largest first), so a central
    /// ignore rule can silence a noisy task completely (see ScheduledTaskIgnoreMatcher.ApplyCentral).
    /// Null = written by an older worker without groups.
    /// </summary>
    public List<ScheduledTaskEventGroup>? EventGroups { get; set; }

    /// <summary>
    /// Only set after a failed query: the tasks of the last successful query, carried forward so the
    /// NumberOfMissedRuns counter and <see cref="ScheduledTaskInfo.MissedRunsIncreased"/> survive the outage.
    /// Not for display — <see cref="Tasks"/> stays empty.
    /// </summary>
    public List<ScheduledTaskInfo>? CarriedTasks { get; set; }

    /// <summary>
    /// Flat copy without <see cref="CarriedTasks"/> for the HTTP status JSON: the carry-forward is only needed by
    /// the worker (read from its own status file), not by the central WebUI / notifier. The original is unchanged.
    /// </summary>
    public ScheduledTaskState WithoutCarriedTasks()
    {
        var copy = (ScheduledTaskState)MemberwiseClone();
        copy.CarriedTasks = null;
        return copy;
    }

    /// <summary>User-facing reason for <see cref="EventLogDisabled"/>.</summary>
    public const string EventLogDisabledReason = "Aufgabenverlauf ist deaktiviert – Fehler-Ereignisse werden nicht erkannt";
}

/// <summary>State and evaluation of one scheduled task.</summary>
public class ScheduledTaskInfo
{
    /// <summary>Full task path incl. name, e.g. "\Minicon\Export". Identity across servers.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Task name (last path segment).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Task description, if any.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the task is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Task Scheduler state: Ready, Running, Disabled, Queued or Unknown.</summary>
    public string State { get; set; } = "Unknown";

    /// <summary>Account the task runs as (principal), if known.</summary>
    public string? RunAs { get; set; }

    /// <summary>Start of the last run (UTC), null = never ran.</summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>Result of the last run (Win32/HRESULT as signed int), null = unknown.</summary>
    public int? LastTaskResult { get; set; }

    /// <summary>Next scheduled run (UTC), null = no time trigger.</summary>
    public DateTime? NextRunTime { get; set; }

    /// <summary>Start of the current run (UTC) when <see cref="State"/> is Running.</summary>
    public DateTime? RunningSince { get; set; }

    /// <summary>Cross-server mode from the matching watch pattern.</summary>
    public ScheduledTaskMode Mode { get; set; } = ScheduledTaskMode.SingleActive;

    /// <summary>The watch pattern that selected this task.</summary>
    public string? MatchedPattern { get; set; }

    /// <summary>Evaluation: last run result is an error.</summary>
    public bool LastRunFailed { get; set; }

    /// <summary>Evaluation: severity of <see cref="LastRunFailed"/>.</summary>
    public bool FailureIsCritical { get; set; } = true;

    /// <summary>Evaluation: current run exceeds <see cref="MaxRuntimeMinutes"/>.</summary>
    public bool RuntimeExceeded { get; set; }

    /// <summary>Effective runtime limit in minutes (0 = off).</summary>
    public int MaxRuntimeMinutes { get; set; }

    /// <summary>
    /// Evaluation: scheduled run was missed — enabled, not running/queued, and either NextRunTime is older than
    /// the grace or <see cref="MissedRunsIncreased"/> is set.
    /// </summary>
    public bool MissedRun { get; set; }

    /// <summary>Task Scheduler counter NumberOfMissedRuns (null = unknown).</summary>
    public int? NumberOfMissedRuns { get; set; }

    /// <summary>
    /// Evaluation: <see cref="NumberOfMissedRuns"/> rose compared to the previous worker cycle. Carried forward
    /// (only for enabled tasks) until the task runs again (LastRunTime changes), it has no next run any more,
    /// or 24 h after the rise; no alarm on the first cycle without a previous value.
    /// </summary>
    public bool MissedRunsIncreased { get; set; }

    /// <summary>When <see cref="MissedRunsIncreased"/> was first detected (UTC), for the 24 h expiry.</summary>
    public DateTime? MissedRunsIncreasedAt { get; set; }

    /// <summary>
    /// Error of Get-ScheduledTaskInfo for this task (null = fine). Without the info, last result, next run and
    /// missed runs are unknown, so failures of this task cannot be detected.
    /// </summary>
    public string? InfoError { get; set; }

    /// <summary>Optional wiki/runbook link from the watch pattern.</summary>
    public string? WikiUrl { get; set; }

    /// <summary>Optional remediation steps from the watch pattern.</summary>
    public List<string> RemediationSteps { get; set; } = new();
}

/// <summary>One Task Scheduler event (Microsoft-Windows-TaskScheduler/Operational).</summary>
public class ScheduledTaskEvent
{
    /// <summary>When the event occurred (UTC).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Event ID (e.g. 101 start failed, 103 action failed, 111 terminated, 203 action launch failed, 329 timeout).</summary>
    public int EventId { get; set; }

    /// <summary>Event level (Critical, Error, Warning).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Task path the event refers to (empty if not resolvable).</summary>
    public string TaskPath { get; set; } = string.Empty;

    /// <summary>Event message text (truncated).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Activity ID of the task run the event belongs to (groups the events of one run), if known.</summary>
    public string? ActivityId { get; set; }
}

/// <summary>Non-ignored Task Scheduler error events of one task and event ID within the look-back window.</summary>
public class ScheduledTaskEventGroup
{
    /// <summary>Task path, e.g. "\Minicon\Export".</summary>
    public string TaskPath { get; set; } = string.Empty;

    /// <summary>Event ID of the run's representative event.</summary>
    public int EventId { get; set; }

    /// <summary>Number of task runs (after de-duplication) with this event.</summary>
    public int Count { get; set; }

    /// <summary>Newest occurrence (UTC).</summary>
    public DateTime LastSeen { get; set; }

    /// <summary>Message of the newest occurrence (truncated); MessageContains rules are matched against it.</summary>
    public string SampleMessage { get; set; } = string.Empty;
}
