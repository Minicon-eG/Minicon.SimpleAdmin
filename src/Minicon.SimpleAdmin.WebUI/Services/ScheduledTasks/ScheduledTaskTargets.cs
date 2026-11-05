using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;
using static Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// Validates control targets against config.json and the reported task groups: only active servers with
/// scheduled task monitoring and task paths the servers actually reported can be acted on — the WebUI
/// never contacts a host or task path that came only from the client.
/// </summary>
internal static class ScheduledTaskTargets
{
    /// <summary>Active config server (by name or ID) with scheduled task monitoring enabled, else null.</summary>
    public static Server? ResolveServer(Config config, string? serverRef)
    {
        if (string.IsNullOrWhiteSpace(serverRef))
            return null;
        var trimmed = serverRef.Trim();
        return config.Servers.FirstOrDefault(s =>
            s.Active
            && s.Checks?.ScheduledTasks?.Enabled == true
            && (string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.GetEffectiveId(), trimmed, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when the group's server ID (from the status data) denotes the config server.</summary>
    public static bool IsServer(string serverId, Server server) =>
        string.Equals(serverId, server.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverId, server.GetEffectiveId(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The group of <paramref name="taskPath"/> the server takes part in — as a member that reported the task
    /// or as an unknown server of the group's environment (its state is not known, it may have the task).
    /// </summary>
    public static TaskGroup? FindGroup(IReadOnlyList<TaskGroup> groups, Server server, string? taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath))
            return null;
        var path = taskPath.Trim();
        return groups
            .Where(g => string.Equals(g.TaskPath, path, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(g => g.Members.Any(m => IsServer(m.ServerId, server))
                                 || g.UnknownServers.Any(u => IsServer(u, server)));
    }

    /// <summary>
    /// Config names of every server of the group: members plus unknown servers (an unknown server may have
    /// the task enabled, so "activate exclusively" must try to disable it there as well).
    /// </summary>
    public static List<string> GroupServerNames(Config config, TaskGroup group) =>
        group.Members.Select(m => m.ServerId)
            .Concat(group.UnknownServers)
            .Select(id => ResolveServer(config, id)?.Name)
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Server IDs of the group (members + unknown) that do not resolve to an active config server with task monitoring.</summary>
    public static List<string> UnresolvedServers(Config config, TaskGroup group) =>
        group.Members.Select(m => m.ServerId)
            .Concat(group.UnknownServers)
            .Where(id => ResolveServer(config, id) == null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Config names of the servers whose worker reported the task (group members, without unknown servers).</summary>
    public static List<string> ReportingServerNames(Config config, TaskGroup group) =>
        group.Members
            .Select(m => ResolveServer(config, m.ServerId)?.Name)
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Single-active finding of a group (mirrors <see cref="ScheduledTaskGroupEvaluator.DeriveGroupProblems"/>).</summary>
    public static ScheduledTaskViolation Violation(TaskGroup group)
    {
        if (group.Mode != ScheduledTaskMode.SingleActive)
            return ScheduledTaskViolation.None;
        var active = group.ActiveServers.Count;
        if (active > 1) return ScheduledTaskViolation.MultipleActive;
        if (active == 0) return group.UnknownServers.Count == 0
            ? ScheduledTaskViolation.NoneActive
            : ScheduledTaskViolation.PossiblyNoneActive;
        return ScheduledTaskViolation.None;
    }
}

/// <summary>Single-active state of a task group.</summary>
public enum ScheduledTaskViolation
{
    /// <summary>No finding (or mode AllServers).</summary>
    None,

    /// <summary>Enabled on more than one server.</summary>
    MultipleActive,

    /// <summary>Enabled on no server, every server of the environment is known.</summary>
    NoneActive,

    /// <summary>Enabled on no known server, but some servers of the environment are unknown.</summary>
    PossiblyNoneActive
}
