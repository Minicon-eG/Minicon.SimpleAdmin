using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Interface for checking Windows Scheduled Tasks (state, last result, runtime, missed runs)
/// and the Task Scheduler error events of the watched tasks.
/// </summary>
public interface IScheduledTaskChecker
{
    /// <summary>
    /// Queries the watched scheduled tasks of this server and evaluates them.
    /// Never throws for query problems: they are reported via <see cref="ScheduledTaskState.QueryFailed"/>.
    /// On non-Windows an empty state is returned.
    /// </summary>
    /// <param name="config">Per-server scheduled task configuration (watch patterns)</param>
    /// <param name="defaults">Global defaults (runtime limit, missed-run grace, event thresholds)</param>
    /// <param name="ignoreRules">
    /// Active ignore rules applicable to this server (already filtered by server scope).
    /// Matching events are excluded from the event thresholds.
    /// </param>
    /// <param name="previous">
    /// State of the previous worker cycle (from the last RuntimeStatus): the NumberOfMissedRuns counter is
    /// compared against it. Null on the first cycle — then a counter value raises no alarm.
    /// </param>
    /// <param name="cancellationToken">Cancels the query (the PowerShell process is killed)</param>
    Task<ScheduledTaskState> CheckAsync(
        ServerScheduledTasksConfig config,
        ScheduledTaskDefaults defaults,
        IReadOnlyList<ScheduledTaskIgnoreRule>? ignoreRules = null,
        ScheduledTaskState? previous = null,
        CancellationToken cancellationToken = default);
}
