using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// Remote control of Windows Scheduled Tasks from the WebUI process (acts directly on the target server;
/// the WebUI's service account needs the rights there). Every mutating call is written to the audit log.
/// </summary>
public interface IScheduledTaskControl
{
    /// <summary>Whether control actions are allowed on this host (false while authentication is disabled).</summary>
    bool IsEnabled { get; }

    /// <summary>German reason when <see cref="IsEnabled"/> is false.</summary>
    string? DisabledReason { get; }

    /// <summary>Executes one action on one server and audits it.</summary>
    Task<ScheduledTaskActionResult> ExecuteAsync(
        string server, string taskPath, ScheduledTaskAction action, string user, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="targetServer"/> the only server with the task enabled: checks the target first
    /// (nothing is changed when it is unreachable or lacks the task), disables it on every other server in
    /// <paramref name="groupServers"/>, then enables it on the target. Partial failures are reported per
    /// server; the target is NOT enabled when a disable step failed (otherwise the task could run twice).
    /// <paramref name="ct"/> is honoured only until the change starts — once the first server is touched the
    /// operation always completes, so an aborted request cannot leave the task disabled everywhere.
    /// </summary>
    /// <param name="serversReportingTask">
    /// Servers on which the worker reports the task (optional). "Task not found" on one of these is treated as
    /// an error (the WebUI account may simply not see it) and blocks the activation; on other servers it counts
    /// as "not present".
    /// </param>
    Task<ScheduledTaskExclusiveResult> ActivateExclusivelyAsync(
        string taskPath, string targetServer, IReadOnlyList<string> groupServers, string user,
        IReadOnlyCollection<string>? serversReportingTask = null, CancellationToken ct = default);

    /// <summary>Queries the current state of a task on a server (no audit).</summary>
    Task<ScheduledTaskLiveState> QueryAsync(string server, string taskPath, CancellationToken ct = default);

    /// <summary>Newest audit entries first.</summary>
    IReadOnlyList<ScheduledTaskAuditEntry> GetAuditLog(int max = 200, string? taskPath = null);
}
