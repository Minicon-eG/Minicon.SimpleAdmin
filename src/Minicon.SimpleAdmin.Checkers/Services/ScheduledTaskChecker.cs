using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks Windows Scheduled Tasks via PowerShell (Get-ScheduledTask + Get-ScheduledTaskInfo) and the
/// Task Scheduler error events (Microsoft-Windows-TaskScheduler/Operational) of the watched tasks.
/// PowerShell only delivers raw data; selection and evaluation happen in C# (<see cref="Evaluate"/>),
/// pure and testable with an injected clock. Returns an empty state on non-Windows.
/// <para>
/// <b>Rights:</b> the worker must run as local administrator or SYSTEM. Get-ScheduledTask only returns the
/// tasks the account may read — tasks of other users or with restrictive security descriptors are silently
/// missing for a normal account (a watch pattern then reports 0 hits, see
/// <see cref="ScheduledTaskState.PatternMatches"/>), and reading the TaskScheduler/Operational log needs
/// at least "Event Log Readers".
/// </para>
/// <para>
/// <b>Parallel instances / ExecutionTimeLimit:</b> the Task Scheduler reports one state per task. With
/// MultipleInstances = Parallel, State is Running while any instance runs and LastRunTime is the start of the
/// newest instance, so <see cref="ScheduledTaskInfo.RunningSince"/> underestimates the runtime of an older
/// instance that is still running. A task's own ExecutionTimeLimit stops it independently of
/// <see cref="ScheduledTaskDefaults.MaxRuntimeMinutes"/>; that stop shows up as event 111/329 (counted once
/// per run) and usually as a non-zero LastTaskResult.
/// </para>
/// </summary>
public sealed class ScheduledTaskChecker : IScheduledTaskChecker
{
    /// <summary>Upper bound for one PowerShell query (incl. PowerShell start-up).</summary>
    internal static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Upper bound of Task Scheduler events fetched per cycle (newest first). The query is already restricted
    /// to the IDs in <see cref="QueriedEventIds"/>, so 500 is far above the normal volume of the default 60 min
    /// window; the limit bounds the query time during an event storm. Beyond it only the oldest events are
    /// lost and the count becomes a lower bound — still far above any sensible critical threshold.
    /// </summary>
    internal const int MaxQueriedEvents = 500;

    /// <summary>At most this many events per task are reported, so one noisy task cannot crowd out the others.</summary>
    internal const int MaxReportedEventsPerTask = 10;

    /// <summary>Cap on <see cref="ScheduledTaskState.EventGroups"/> (largest groups first).</summary>
    internal const int MaxEventGroups = 200;

    /// <summary>Event messages are cut to this length (keeps the HTTP status payload small).</summary>
    internal const int MaxMessageLength = 500;

    /// <summary>Task folder that is only selected when the include pattern explicitly starts with it.</summary>
    internal const string MicrosoftFolder = @"\Microsoft\";

    internal const string TaskSchedulerLogName = "Microsoft-Windows-TaskScheduler/Operational";

    /// <summary>Event 330 "task stopping due to user request": an admin stop, suppresses event 111 of the same run.</summary>
    internal const int StoppedByUserEventId = 330;

    /// <summary>Event 111 "task terminated": not counted when the run was stopped by a user (330).</summary>
    internal const int TaskTerminatedEventId = 111;

    /// <summary>
    /// Task Scheduler event IDs (levels Critical/Error/Warning) that mean a task did not run or did not
    /// finish properly. Deliberately NOT included: 322/324 (launch ignored/queued because an instance is
    /// already running — intended behaviour), 330 (stopped on user request — an admin stop is no error),
    /// 311 (task engine failure, carries no task path) and all informational events.
    /// Several of these events belong to one run (e.g. 329 + 111, or 101 + 103 + 203); they are counted once
    /// per run (<see cref="EvaluateEvents"/>).
    /// </summary>
    internal static readonly IReadOnlyList<int> ErrorEventIds = new[]
    {
        101, // Task start failed
        103, // Action start failed
        111, // Task terminated (e.g. execution time limit)
        202, // Action failed
        203, // Action failed to start
        204, // Task failed to retrieve event trigger values
        205, // Task failed to match the event trigger pattern
        329, // Task stopping due to timeout reached
        332  // Launch condition not met, user not logged on
    };

    /// <summary>Event IDs fetched from the log: the error IDs plus 330 (needed to recognise admin stops).</summary>
    internal static IEnumerable<int> QueriedEventIds => ErrorEventIds.Append(StoppedByUserEventId);

    /// <summary>
    /// LastTaskResult codes that are informational (SCHED_S_*) or otherwise no failure, see winerror.h.
    /// 0x41306 (terminated by the user) counts as success: ending a task is an admin decision, not an error.
    /// 0x4131C (SCHED_S_BATCH_LOGON_PROBLEM) is left out on purpose — it signals a real logon right problem.
    /// </summary>
    internal static readonly IReadOnlySet<int> InformationalResultCodes = new HashSet<int>
    {
        0x00041300, // SCHED_S_TASK_READY
        0x00041301, // SCHED_S_TASK_RUNNING
        0x00041302, // SCHED_S_TASK_DISABLED
        0x00041303, // SCHED_S_TASK_HAS_NOT_RUN
        0x00041304, // SCHED_S_TASK_NO_MORE_RUNS
        0x00041305, // SCHED_S_TASK_NOT_SCHEDULED
        0x00041306, // SCHED_S_TASK_TERMINATED (ended by user/admin)
        0x00041307, // SCHED_S_TASK_NO_VALID_TRIGGERS
        0x00041308, // SCHED_S_EVENT_TRIGGER
        0x0004131B, // SCHED_S_SOME_TRIGGERS_FAILED
        0x00041325, // SCHED_S_TASK_QUEUED
        unchecked((int)0x8004131F) // SCHED_E_ALREADY_RUNNING
    };

    private readonly ILogger<ScheduledTaskChecker> _logger;
    private readonly bool _isWindows;
    private readonly Func<string, TimeSpan, CancellationToken, Task<PowerShellOutput>> _runScript;

    public ScheduledTaskChecker(ILogger<ScheduledTaskChecker> logger)
        : this(logger, RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            (script, timeout, ct) => RunPowerShellAsync("powershell.exe", script, timeout, ct))
    {
    }

    /// <summary>Test seam: platform flag and the PowerShell runner are injectable.</summary>
    internal ScheduledTaskChecker(
        ILogger<ScheduledTaskChecker> logger,
        bool isWindows,
        Func<string, TimeSpan, CancellationToken, Task<PowerShellOutput>> runScript)
    {
        _logger = logger;
        _isWindows = isWindows;
        _runScript = runScript;
    }

    /// <summary>Query timeout; the PowerShell process is killed afterwards (test seam).</summary>
    internal TimeSpan QueryTimeout { get; init; } = DefaultQueryTimeout;

    /// <inheritdoc />
    public async Task<ScheduledTaskState> CheckAsync(
        ServerScheduledTasksConfig config,
        ScheduledTaskDefaults defaults,
        IReadOnlyList<ScheduledTaskIgnoreRule>? ignoreRules = null,
        ScheduledTaskState? previous = null,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;

        if (!_isWindows)
        {
            _logger.LogDebug("Not running on Windows, returning empty Scheduled Task results");
            return new ScheduledTaskState { LastChecked = nowUtc };
        }

        var prefixes = BuildPathPrefixes(config.Tasks);
        if (!config.Enabled || prefixes.Count == 0)
        {
            return new ScheduledTaskState { LastChecked = nowUtc };
        }

        var script = BuildQueryScript(prefixes, config.CheckEvents, Math.Max(1, defaults.EventTimeWindowMinutes));

        PowerShellOutput output;
        try
        {
            output = await _runScript(script, QueryTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogWarning(ex, "Scheduled task query: powershell.exe not found");
            return Failed(previous, nowUtc, "powershell.exe nicht gefunden");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled task query could not be started");
            return Failed(previous, nowUtc, $"PowerShell konnte nicht gestartet werden: {ex.Message}");
        }

        if (output.TimedOut)
        {
            _logger.LogWarning("Scheduled task query timed out after {Timeout} and was killed", QueryTimeout);
            return Failed(previous, nowUtc, $"Zeitüberschreitung der Task-Abfrage nach {QueryTimeout.TotalSeconds:0} s");
        }

        if (!string.IsNullOrWhiteSpace(output.StdErr))
            _logger.LogDebug("Scheduled task query wrote to stderr: {Stderr}", output.StdErr.Trim());

        ScheduledTaskQueryResult raw;
        try
        {
            raw = ParseQueryOutput(output.StdOut);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Scheduled task query returned invalid output (exit code {ExitCode})", output.ExitCode);
            return Failed(previous, nowUtc, output.ExitCode != 0
                ? $"Task-Abfrage fehlgeschlagen (Exit-Code {output.ExitCode}): {FirstLine(output.StdErr)}"
                : "Task-Abfrage lieferte keine gültige Ausgabe");
        }

        if (raw.QueryError != null)
        {
            _logger.LogWarning("Scheduled task query failed: {Error}", raw.QueryError);
            return Failed(previous, nowUtc, $"Task-Abfrage fehlgeschlagen: {Truncate(raw.QueryError, 300)}");
        }

        // The script writes its JSON as the very last step; complete JSON without queryError is trustworthy
        // even if PowerShell exits non-zero afterwards (e.g. a late non-terminating error).
        if (output.ExitCode != 0)
            _logger.LogWarning("Scheduled task query exited with code {ExitCode} but returned valid data: {Stderr}",
                output.ExitCode, FirstLine(output.StdErr));

        if (raw.EventError != null)
            _logger.LogWarning("Task Scheduler events could not be read: {Error}", raw.EventError);
        if (raw.EventLogDisabled && config.CheckEvents)
            _logger.LogWarning("Task Scheduler history ({Log}) is disabled — error events cannot be detected", TaskSchedulerLogName);
        if (raw.Events.Count >= MaxQueriedEvents)
            _logger.LogDebug("Task Scheduler event query hit the limit of {Max} events; older events were not read", MaxQueriedEvents);

        var state = Evaluate(config, defaults, raw, ignoreRules, nowUtc, previous);

        _logger.LogDebug("Scheduled tasks: {Count} watched task(s), {Events} event(s) ({Ignored} ignored), event status {Status}",
            state.Tasks.Count, state.EventCount, state.IgnoredEventCount, state.EventStatus);

        return state;
    }

    /// <summary>
    /// State for a failed query. The tasks of the last successful query travel along in
    /// <see cref="ScheduledTaskState.CarriedTasks"/>, so the missed-run counter survives the outage.
    /// </summary>
    internal static ScheduledTaskState Failed(ScheduledTaskState? previous, DateTime nowUtc, string error)
        => new() { LastChecked = nowUtc, QueryFailed = true, Error = error, CarriedTasks = LastKnownTasks(previous) };

    /// <summary>Tasks of the last successful query: the previous tasks, or what a failed previous cycle carried.</summary>
    internal static List<ScheduledTaskInfo>? LastKnownTasks(ScheduledTaskState? previous)
        => previous == null ? null
            : previous.Tasks.Count > 0 ? previous.Tasks
            : previous.CarriedTasks;

    /// <summary>Filters the central ignore rules to those that apply to the given server.</summary>
    public static List<ScheduledTaskIgnoreRule> RulesForServer(IEnumerable<ScheduledTaskIgnoreRule>? rules, string serverName)
        => (rules ?? Enumerable.Empty<ScheduledTaskIgnoreRule>())
            .Where(r => ScheduledTaskIgnoreMatcher.RuleAppliesToServer(r, serverName))
            .ToList();

    // ---------------------------------------------------------------------------------------------
    // Pure evaluation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Selects the watched tasks, evaluates them and the Task Scheduler events. Pure: <paramref name="nowUtc"/>
    /// is injected. <paramref name="ignoreRules"/> must already be filtered by server scope;
    /// <paramref name="previous"/> is the state of the previous worker cycle (missed-run counter carry-forward).
    /// </summary>
    internal static ScheduledTaskState Evaluate(
        ServerScheduledTasksConfig config,
        ScheduledTaskDefaults defaults,
        ScheduledTaskQueryResult raw,
        IReadOnlyList<ScheduledTaskIgnoreRule>? ignoreRules,
        DateTime nowUtc,
        ScheduledTaskState? previous = null)
    {
        var state = new ScheduledTaskState { LastChecked = nowUtc };
        foreach (var watch in config.Tasks.Where(w => !string.IsNullOrWhiteSpace(w.Pattern)))
            state.PatternMatches[watch.Pattern] = 0;

        var previousTasks = new Dictionary<string, ScheduledTaskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in LastKnownTasks(previous) ?? Enumerable.Empty<ScheduledTaskInfo>())
            previousTasks[p.Path] = p;

        foreach (var task in raw.Tasks)
        {
            var path = BuildTaskPath(task.TaskPath, task.TaskName);
            var watch = FindWatch(path, config.Tasks);
            if (watch == null)
                continue;
            state.PatternMatches[watch.Pattern] = state.PatternMatches.GetValueOrDefault(watch.Pattern) + 1;

            var info = new ScheduledTaskInfo
            {
                Path = path,
                Name = task.TaskName ?? string.Empty,
                Description = string.IsNullOrWhiteSpace(task.Description) ? null : task.Description,
                State = NormalizeState(task.State),
                RunAs = string.IsNullOrWhiteSpace(task.UserId) ? null : task.UserId,
                LastRunTime = task.LastRunTime,
                LastTaskResult = task.LastTaskResult,
                NextRunTime = task.NextRunTime,
                NumberOfMissedRuns = task.NumberOfMissedRuns,
                InfoError = string.IsNullOrWhiteSpace(task.InfoError) ? null : Truncate(task.InfoError.Trim(), 300)
            };
            info.Enabled = (task.Enabled ?? true) && !info.State.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

            EvaluateTask(info, watch, defaults, nowUtc, previousTasks.GetValueOrDefault(path));
            state.Tasks.Add(info);
        }

        state.Tasks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));

        if (config.CheckEvents)
        {
            state.EventLogDisabled = raw.EventLogDisabled;
            state.EventError = raw.EventError == null ? null : Truncate(raw.EventError.Trim(), 300);
            EvaluateEvents(state, raw.Events, defaults, ignoreRules, nowUtc);
        }

        return state;
    }

    /// <summary>
    /// Sets the evaluation flags of one task: last run failed, runtime exceeded, missed run.
    /// Only an enabled task can fail or miss a run; a running or never-run task has no failure; a running or
    /// queued task is never a missed run. <paramref name="previous"/> is the same task of the previous cycle.
    /// </summary>
    internal static void EvaluateTask(
        ScheduledTaskInfo info,
        ScheduledTaskWatchConfig watch,
        ScheduledTaskDefaults defaults,
        DateTime nowUtc,
        ScheduledTaskInfo? previous = null)
    {
        var running = info.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var queued = info.State.Equals("Queued", StringComparison.OrdinalIgnoreCase);

        info.Mode = watch.Mode;
        info.MatchedPattern = watch.Pattern;
        info.WikiUrl = string.IsNullOrWhiteSpace(watch.WikiUrl) ? null : watch.WikiUrl;
        info.RemediationSteps = watch.RemediationSteps.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        info.FailureIsCritical = watch.FailureIsCritical;

        info.RunningSince = running ? info.LastRunTime : null;
        info.MaxRuntimeMinutes = Math.Max(0, watch.MaxRuntimeMinutes ?? defaults.MaxRuntimeMinutes);
        info.RuntimeExceeded = running
                               && info.RunningSince.HasValue
                               && info.MaxRuntimeMinutes > 0
                               && nowUtc - info.RunningSince.Value > TimeSpan.FromMinutes(info.MaxRuntimeMinutes);

        info.LastRunFailed = info.Enabled
                             && !running
                             && info.LastRunTime.HasValue
                             && info.LastTaskResult.HasValue
                             && !IsSuccessResult(info.LastTaskResult.Value, defaults.AllowedResultCodes, watch.AllowedResultCodes);

        EvaluateMissedRunCounter(info, previous, nowUtc);

        var grace = TimeSpan.FromMinutes(Math.Max(0, watch.MissedRunGraceMinutes ?? defaults.MissedRunGraceMinutes));
        var nextRunOverdue = info.NextRunTime.HasValue && info.NextRunTime.Value < nowUtc - grace;
        info.MissedRun = watch.CheckMissedRun
                         && info.Enabled
                         && !running
                         && !queued
                         && (nextRunOverdue || info.MissedRunsIncreased);
    }

    /// <summary>Missed-run flag set by a rise of the counter expires after this time.</summary>
    internal static readonly TimeSpan MissedRunsIncreasedExpiry = TimeSpan.FromHours(24);

    /// <summary>
    /// Missed-run counter carry-forward (enabled tasks only): a rise of NumberOfMissedRuns against the previous
    /// cycle sets <see cref="ScheduledTaskInfo.MissedRunsIncreased"/>; it stays set while the task has not run
    /// again (LastRunTime unchanged) and expires when the task has no next run any more or 24 h after the rise.
    /// Without a current counter (Get-ScheduledTaskInfo failed) the last known counter and flag are kept.
    /// No previous value (first cycle, new task) = no alarm.
    /// </summary>
    internal static void EvaluateMissedRunCounter(ScheduledTaskInfo info, ScheduledTaskInfo? previous, DateTime nowUtc)
    {
        info.MissedRunsIncreased = false;
        info.MissedRunsIncreasedAt = null;
        if (!info.Enabled || previous == null)
            return;

        if (info.NumberOfMissedRuns == null)
        {
            // Counter unknown this cycle: keep the last known value and flag (the 24 h expiry still applies).
            info.NumberOfMissedRuns = previous.NumberOfMissedRuns;
            if (previous.MissedRunsIncreased && previous.MissedRunsIncreasedAt is { } since && nowUtc - since <= MissedRunsIncreasedExpiry)
            {
                info.MissedRunsIncreased = true;
                info.MissedRunsIncreasedAt = since;
            }
            return;
        }

        if (previous.NumberOfMissedRuns is not { } before)
            return;

        var now = info.NumberOfMissedRuns.Value;
        DateTime? increasedAt = null;
        if (now > before)
            increasedAt = nowUtc;
        else if (previous.MissedRunsIncreased && now >= before && info.LastRunTime == previous.LastRunTime)
            increasedAt = previous.MissedRunsIncreasedAt ?? nowUtc;

        if (increasedAt.HasValue && info.NextRunTime.HasValue && nowUtc - increasedAt.Value <= MissedRunsIncreasedExpiry)
        {
            info.MissedRunsIncreased = true;
            info.MissedRunsIncreasedAt = increasedAt;
        }
    }

    /// <summary>
    /// True when the LastTaskResult is no failure: 0, an informational code (<see cref="InformationalResultCodes"/>)
    /// or one of the globally / per pattern allowed codes.
    /// </summary>
    internal static bool IsSuccessResult(int code, IEnumerable<int>? globalAllowed = null, IEnumerable<int>? watchAllowed = null)
        => code == 0
           || InformationalResultCodes.Contains(code)
           || (globalAllowed?.Contains(code) ?? false)
           || (watchAllowed?.Contains(code) ?? false);

    /// <summary>
    /// Returns the watch pattern selecting <paramref name="taskPath"/> (null = not watched). Wildcard * / ?,
    /// case-insensitive, on the full path. The first pattern whose include matches decides finally: if one of
    /// its excludes matches too, the task is not watched and no later pattern is consulted. Tasks below
    /// \Microsoft\ only match patterns that themselves start with \Microsoft\.
    /// </summary>
    internal static ScheduledTaskWatchConfig? FindWatch(string taskPath, IEnumerable<ScheduledTaskWatchConfig> watches)
    {
        var path = ScheduledTaskPaths.NormalizePath(taskPath);
        var isMicrosoftTask = path.StartsWith(MicrosoftFolder, StringComparison.OrdinalIgnoreCase);

        foreach (var watch in watches)
        {
            var pattern = ScheduledTaskPaths.NormalizePattern(watch.Pattern);
            if (pattern.Length == 0)
                continue;
            if (isMicrosoftTask && !pattern.StartsWith(MicrosoftFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!EventLogIgnoreMatcher.MatchesWildcard(path, pattern))
                continue;

            var excluded = watch.ExcludePatterns.Any(x => !string.IsNullOrWhiteSpace(x)
                                                          && EventLogIgnoreMatcher.MatchesWildcard(path, ScheduledTaskPaths.NormalizePattern(x)));
            return excluded ? null : watch;
        }

        return null;
    }

    /// <summary>
    /// Literal path prefixes (up to the first wildcard) of all include patterns. PowerShell uses them only
    /// as a coarse pre-filter so Get-ScheduledTaskInfo and the event messages are not read for every system
    /// task; the authoritative selection is <see cref="FindWatch"/>.
    /// </summary>
    internal static List<string> BuildPathPrefixes(IEnumerable<ScheduledTaskWatchConfig> watches)
        => watches
            .Select(w => ScheduledTaskPaths.NormalizePattern(w.Pattern))
            .Where(p => p.Length > 0)
            .Select(p =>
            {
                var wildcard = p.IndexOfAny(new[] { '*', '?' });
                return wildcard < 0 ? p : p[..wildcard];
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Full task path: folder (with trailing backslash) + name, e.g. "\Minicon\" + "Export".</summary>
    internal static string BuildTaskPath(string? folder, string? name)
    {
        var f = ScheduledTaskPaths.NormalizePath(folder);
        if (f.Length == 0) f = "\\";
        if (!f.EndsWith('\\')) f += "\\";
        return f + (name ?? string.Empty);
    }

    /// <summary>Maps the Task Scheduler state (name or numeric StateEnum value) to Ready/Running/Disabled/Queued/Unknown.</summary>
    internal static string NormalizeState(string? state) => (state ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ready" or "3" => "Ready",
        "running" or "4" => "Running",
        "disabled" or "1" => "Disabled",
        "queued" or "2" => "Queued",
        _ => "Unknown"
    };

    /// <summary>
    /// Counts the error events of the watched tasks and sets <see cref="ScheduledTaskState.EventCount"/>,
    /// <see cref="ScheduledTaskState.IgnoredEventCount"/>, <see cref="ScheduledTaskState.EventStatus"/> and
    /// <see cref="ScheduledTaskState.RecentEvents"/>.
    /// <list type="number">
    /// <item>Only error IDs of watched tasks inside the window count.</item>
    /// <item>111 (terminated) is dropped when the same run was stopped by a user (330).</item>
    /// <item>One run counts once: events are grouped by (task, ActivityId) — without ActivityId by task and
    /// a one-minute time cluster (<see cref="AssignRunKeys"/>) — and the most severe event represents the run.</item>
    /// <item>Ignore rules are applied to these representatives (the events shown in the UI).</item>
    /// <item><see cref="ScheduledTaskState.EventGroups"/>: all remaining runs aggregated per (task, event ID),
    /// uncapped per task (at most <see cref="MaxEventGroups"/> groups) — basis for central ignore rules.</item>
    /// <item>Reported: at most <see cref="MaxReportedEventsPerTask"/> per task, newest first, in total
    /// <see cref="ScheduledTaskDefaults.MaxReportedEvents"/>.</item>
    /// </list>
    /// </summary>
    internal static void EvaluateEvents(
        ScheduledTaskState state,
        IEnumerable<RawScheduledTaskEvent> events,
        ScheduledTaskDefaults defaults,
        IReadOnlyList<ScheduledTaskIgnoreRule>? ignoreRules,
        DateTime nowUtc)
    {
        var watched = new HashSet<string>(state.Tasks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var windowStart = nowUtc.AddMinutes(-Math.Max(1, defaults.EventTimeWindowMinutes));

        var relevant = events
            .Select(e => (Raw: e, Path: ScheduledTaskPaths.NormalizePath(e.TaskPath), Activity: NormalizeActivity(e.ActivityId)))
            .Where(e => e.Path.Length > 0 && watched.Contains(e.Path))
            .Where(e => !e.Raw.TimeCreated.HasValue || e.Raw.TimeCreated.Value >= windowStart)
            .ToList();

        var userStops = relevant.Where(e => e.Raw.Id == StoppedByUserEventId).ToList();

        var errors = relevant
            .Where(e => ErrorEventIds.Contains(e.Raw.Id) && e.Raw.Level is >= 1 and <= 3)
            .Where(e => e.Raw.Id != TaskTerminatedEventId || !userStops.Any(s => SameRun(s, e)))
            .ToList();

        var representatives = AssignRunKeys(errors)
            .GroupBy(e => e.RunKey)
            .Select(g => g.Select(x => x.Event)
                .OrderBy(e => e.Raw.Level)
                .ThenByDescending(e => e.Raw.TimeCreated ?? DateTime.MinValue)
                .First())
            .Select(e => new ScheduledTaskEvent
            {
                Timestamp = e.Raw.TimeCreated ?? nowUtc,
                EventId = e.Raw.Id,
                Level = e.Raw.Level switch { 1 => "Critical", 2 => "Error", _ => "Warning" },
                TaskPath = e.Path,
                Message = Truncate((e.Raw.Message ?? string.Empty).Trim(), MaxMessageLength),
                ActivityId = e.Activity
            })
            .ToList();

        var remaining = new List<ScheduledTaskEvent>();
        var ignored = 0;
        foreach (var ev in representatives)
        {
            if (ScheduledTaskIgnoreMatcher.IsIgnored(ev, ignoreRules, nowUtc))
                ignored++;
            else
                remaining.Add(ev);
        }

        state.EventCount = remaining.Count;
        state.IgnoredEventCount = ignored;
        state.EventStatus = ScheduledTaskIgnoreMatcher.EvaluateEventCount(remaining.Count, defaults);
        state.EventGroups = remaining
            .GroupBy(e => (Path: e.TaskPath.ToUpperInvariant(), e.EventId))
            .Select(g =>
            {
                var newest = g.OrderByDescending(e => e.Timestamp).First();
                return new ScheduledTaskEventGroup
                {
                    TaskPath = newest.TaskPath,
                    EventId = newest.EventId,
                    Count = g.Count(),
                    LastSeen = newest.Timestamp,
                    SampleMessage = newest.Message
                };
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastSeen)
            .Take(MaxEventGroups)
            .ToList();
        state.RecentEvents = remaining
            .GroupBy(e => e.TaskPath, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(e => e.Timestamp).Take(MaxReportedEventsPerTask))
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Max(0, defaults.MaxReportedEvents))
            .ToList();
    }

    /// <summary>Same task run: same task and ActivityId, or (when one has none) same task within one minute.</summary>
    private static bool SameRun(
        (RawScheduledTaskEvent Raw, string Path, string? Activity) a,
        (RawScheduledTaskEvent Raw, string Path, string? Activity) b)
    {
        if (!a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase))
            return false;
        if (a.Activity != null && b.Activity != null)
            return a.Activity == b.Activity;
        return a.Raw.TimeCreated.HasValue && b.Raw.TimeCreated.HasValue
               && (a.Raw.TimeCreated.Value - b.Raw.TimeCreated.Value).Duration() <= TimeSpan.FromMinutes(1);
    }

    private static string? NormalizeActivity(string? activityId)
    {
        if (string.IsNullOrWhiteSpace(activityId))
            return null;
        var a = activityId.Trim().Trim('{', '}').ToLowerInvariant();
        return a.Length == 0 || a.All(c => c is '0' or '-') ? null : a;
    }

    /// <summary>
    /// Run key per event: task + ActivityId; without ActivityId the events of a task are clustered by time —
    /// a new run starts when an event lies more than one minute after the first event of the current cluster
    /// (same window as <see cref="SameRun"/>). Events without time form their own run per event ID.
    /// </summary>
    private static IEnumerable<(string RunKey, (RawScheduledTaskEvent Raw, string Path, string? Activity) Event)> AssignRunKeys(
        IEnumerable<(RawScheduledTaskEvent Raw, string Path, string? Activity)> events)
    {
        var list = events.ToList();
        foreach (var e in list.Where(e => e.Activity != null))
            yield return ($"{e.Path.ToUpperInvariant()}|a|{e.Activity}", e);

        foreach (var task in list.Where(e => e.Activity == null).GroupBy(e => e.Path.ToUpperInvariant()))
        {
            var cluster = 0;
            DateTime? clusterStart = null;
            foreach (var e in task.OrderBy(e => e.Raw.TimeCreated ?? DateTime.MaxValue))
            {
                if (e.Raw.TimeCreated is not { } t)
                {
                    yield return ($"{task.Key}|n|{e.Raw.Id}", e);
                    continue;
                }
                if (clusterStart == null || t - clusterStart.Value > TimeSpan.FromMinutes(1))
                {
                    cluster++;
                    clusterStart = t;
                }
                yield return ($"{task.Key}|t|{cluster}", e);
            }
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty).Trim().Split('\n')[0].Trim();
        return line.Length == 0 ? "keine Fehlerausgabe" : Truncate(line, 300);
    }

    // ---------------------------------------------------------------------------------------------
    // PowerShell query
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the query script. The only values from config.json are the path prefixes, embedded via
    /// <see cref="PowerShellLiteral.Quote"/>; everything else is a constant or an integer. Dates are
    /// emitted culture-invariant as UTC round-trip strings; "never" dates (before 2000, e.g. 1999-11-30)
    /// become null. Events are pre-filtered by task path before their (expensive) message is rendered.
    /// A disabled or missing TaskScheduler/Operational log is reported as eventLogDisabled.
    /// </summary>
    internal static string BuildQueryScript(IReadOnlyCollection<string> pathPrefixes, bool includeEvents, int eventWindowMinutes)
    {
        var prefixList = string.Join(", ", pathPrefixes.Select(PowerShellLiteral.Quote));
        var eventIds = string.Join(", ", QueriedEventIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var window = eventWindowMinutes.ToString(CultureInfo.InvariantCulture);
        var maxEvents = MaxQueriedEvents.ToString(CultureInfo.InvariantCulture);
        var events = includeEvents ? "$true" : "$false";

        return $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try {{ [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false }} catch {{ }}
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$ic = [System.StringComparison]::OrdinalIgnoreCase
$prefixes = @({prefixList})
function ConvertTo-UtcText($value) {{
    if ($null -eq $value) {{ return $null }}
    $dt = [datetime]$value
    if ($dt.Year -lt 2000) {{ return $null }}
    return $dt.ToUniversalTime().ToString('o', $inv)
}}
function Test-Selected([string]$full) {{
    if (-not $full.StartsWith('\')) {{ $full = '\' + $full }}
    foreach ($p in $prefixes) {{
        if ($full.StartsWith($p, $ic) -and ((-not $full.StartsWith('{MicrosoftFolder}', $ic)) -or $p.StartsWith('{MicrosoftFolder}', $ic))) {{ return $true }}
    }}
    return $false
}}
$result = [ordered]@{{ tasks = @(); events = @(); queryError = $null; eventError = $null; eventLogDisabled = $false }}
try {{
    $result.tasks = @(foreach ($t in @(Get-ScheduledTask)) {{
        if (-not (Test-Selected ([string]$t.TaskPath + [string]$t.TaskName))) {{ continue }}
        $info = $null; $infoError = $null
        try {{ $info = $t | Get-ScheduledTaskInfo }} catch {{ $infoError = [string]$_.Exception.Message }}
        $lastRun = $null; $lastResult = $null; $nextRun = $null; $missed = $null
        if ($null -ne $info) {{
            $lastRun = ConvertTo-UtcText $info.LastRunTime
            if ($null -ne $info.LastTaskResult) {{ $lastResult = [int64]$info.LastTaskResult }}
            $nextRun = ConvertTo-UtcText $info.NextRunTime
            if ($null -ne $info.NumberOfMissedRuns) {{ $missed = [int64]$info.NumberOfMissedRuns }}
        }}
        $enabled = $null
        if ($null -ne $t.Settings) {{ $enabled = [bool]$t.Settings.Enabled }}
        $userId = $null
        if ($null -ne $t.Principal) {{ $userId = [string]$t.Principal.UserId }}
        [ordered]@{{
            taskPath = [string]$t.TaskPath
            taskName = [string]$t.TaskName
            state = [string]$t.State
            enabled = $enabled
            description = [string]$t.Description
            userId = $userId
            lastRunTime = $lastRun
            lastTaskResult = $lastResult
            nextRunTime = $nextRun
            numberOfMissedRuns = $missed
            infoError = $infoError
        }}
    }})
}} catch {{
    $result.queryError = [string]$_.Exception.Message
}}
if ({events} -and $null -eq $result.queryError) {{
    $log = $null
    try {{
        $log = Get-WinEvent -ListLog '{TaskSchedulerLogName}'
    }} catch {{
        if ([string]$_.FullyQualifiedErrorId -like 'NoMatchingLogsFound*') {{ $result.eventLogDisabled = $true }}
        else {{ $result.eventError = [string]$_.Exception.Message }}
    }}
    if ($null -ne $log -and -not $log.IsEnabled) {{ $result.eventLogDisabled = $true }}
    if ($null -ne $log -and $log.IsEnabled) {{
        try {{
            $filter = @{{ LogName = '{TaskSchedulerLogName}'; Level = @(1, 2, 3, 4); Id = @({eventIds}); StartTime = (Get-Date).AddMinutes(-{window}) }}
            $result.events = @(foreach ($e in @(Get-WinEvent -FilterHashtable $filter -MaxEvents {maxEvents})) {{
                $taskPath = $null
                if ($null -ne $e.Properties -and $e.Properties.Count -gt 0) {{ $taskPath = [string]$e.Properties[0].Value }}
                if ([string]::IsNullOrEmpty($taskPath) -or -not (Test-Selected $taskPath)) {{ continue }}
                $activityId = $null
                if ($null -ne $e.ActivityId) {{ $activityId = [string]$e.ActivityId }}
                [ordered]@{{
                    timeCreated = ConvertTo-UtcText $e.TimeCreated
                    id = [int]$e.Id
                    level = [int]$e.Level
                    taskPath = $taskPath
                    activityId = $activityId
                    message = [string]$e.Message
                }}
            }})
        }} catch {{
            if ([string]$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') {{
                $result.eventError = [string]$_.Exception.Message
            }}
        }}
    }}
}}
$result | ConvertTo-Json -Depth 4 -Compress
";
    }

    /// <summary>
    /// Parses the JSON written by <see cref="BuildQueryScript"/>. Tolerates a single object instead of an
    /// array (ConvertTo-Json unwraps one-element arrays), null values and "never" dates (before 2000).
    /// Throws <see cref="JsonException"/> for empty or non-JSON output.
    /// </summary>
    internal static ScheduledTaskQueryResult ParseQueryOutput(string? output)
    {
        var text = (output ?? string.Empty).Trim().TrimStart('\uFEFF');
        if (text.Length == 0)
            throw new JsonException("Leere Ausgabe der Task-Abfrage");

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var result = new ScheduledTaskQueryResult();

        if (root.ValueKind == JsonValueKind.Array)
        {
            // Bare task list (defensive; the script always wraps it in an object)
            result.Tasks.AddRange(Items(root).Select(ParseTask));
            return result;
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Unerwartete Ausgabe der Task-Abfrage");

        result.QueryError = NullIfEmpty(GetString(root, "queryError"));
        result.EventError = NullIfEmpty(GetString(root, "eventError"));
        result.EventLogDisabled = GetBool(root, "eventLogDisabled") ?? false;
        if (TryGet(root, "tasks", out var tasks))
            result.Tasks.AddRange(Items(tasks).Select(ParseTask));
        if (TryGet(root, "events", out var events))
            result.Events.AddRange(Items(events).Select(ParseEvent));
        return result;
    }

    private static RawScheduledTask ParseTask(JsonElement e) => new()
    {
        TaskPath = GetString(e, "taskPath"),
        TaskName = GetString(e, "taskName"),
        State = GetString(e, "state"),
        Enabled = GetBool(e, "enabled"),
        Description = GetString(e, "description"),
        UserId = GetString(e, "userId"),
        LastRunTime = ParseUtc(GetString(e, "lastRunTime")),
        LastTaskResult = GetResultCode(e, "lastTaskResult"),
        NextRunTime = ParseUtc(GetString(e, "nextRunTime")),
        NumberOfMissedRuns = GetLong(e, "numberOfMissedRuns") is { } m and >= 0 and <= int.MaxValue ? (int)m : null,
        InfoError = NullIfEmpty(GetString(e, "infoError"))
    };

    private static RawScheduledTaskEvent ParseEvent(JsonElement e) => new()
    {
        TimeCreated = ParseUtc(GetString(e, "timeCreated")),
        Id = (int)(GetLong(e, "id") ?? 0),
        Level = (int)(GetLong(e, "level") ?? 0),
        TaskPath = GetString(e, "taskPath"),
        ActivityId = NullIfEmpty(GetString(e, "activityId")),
        Message = GetString(e, "message")
    };

    private static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList(),
        JsonValueKind.Object => new[] { element },
        _ => Array.Empty<JsonElement>()
    };

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null
        };
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null
        };
    }

    private static long? GetLong(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    /// <summary>LastTaskResult arrives as unsigned 32-bit value; stored as signed int (HRESULT bit pattern).</summary>
    private static int? GetResultCode(JsonElement obj, string name)
    {
        var value = GetLong(obj, name);
        return value is >= int.MinValue and <= uint.MaxValue ? unchecked((int)value.Value) : null;
    }

    /// <summary>Parses a round-trip date as UTC; null for empty values and "never" dates before 2000.</summary>
    internal static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return null;
        return dt.Year < 2000 ? null : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Runs the script with -NoProfile -NonInteractive -EncodedCommand (via ArgumentList, no command-line
    /// quoting). stdout and stderr are drained concurrently (a full stderr pipe would block PowerShell);
    /// after <paramref name="timeout"/> the process tree is killed and <see cref="PowerShellOutput.TimedOut"/> is set.
    /// A cancelled <paramref name="cancellationToken"/> also kills the process and throws.
    /// </summary>
    internal static async Task<PowerShellOutput> RunPowerShellAsync(string executable, string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand",
                                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new PowerShellOutput(process.ExitCode, stdout, stderr, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // Observe the pending reads so a faulted read does not surface as an unobserved exception.
            _ = stdoutTask.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = stderrTask.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new PowerShellOutput(-1, string.Empty, string.Empty, TimedOut: true);
        }
    }
}

/// <summary>Result of one PowerShell run.</summary>
internal sealed record PowerShellOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Raw query data delivered by PowerShell, before selection and evaluation.</summary>
internal sealed class ScheduledTaskQueryResult
{
    public List<RawScheduledTask> Tasks { get; } = new();
    public List<RawScheduledTaskEvent> Events { get; } = new();
    public string? QueryError { get; set; }
    public string? EventError { get; set; }
    public bool EventLogDisabled { get; set; }
}

internal sealed class RawScheduledTask
{
    public string? TaskPath { get; init; }
    public string? TaskName { get; init; }
    public string? State { get; init; }
    public bool? Enabled { get; init; }
    public string? Description { get; init; }
    public string? UserId { get; init; }
    public DateTime? LastRunTime { get; init; }
    public int? LastTaskResult { get; init; }
    public DateTime? NextRunTime { get; init; }
    public int? NumberOfMissedRuns { get; init; }
    public string? InfoError { get; init; }
}

internal sealed class RawScheduledTaskEvent
{
    public DateTime? TimeCreated { get; init; }
    public int Id { get; init; }
    public int Level { get; init; }
    public string? TaskPath { get; init; }
    public string? ActivityId { get; init; }
    public string? Message { get; init; }
}
