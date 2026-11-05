using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Central, cross-server evaluation of scheduled tasks: groups the same task path of all servers of one
/// environment and checks the single-active rule. Runs on the snapshots the servers report (WebUI
/// StatusReaderService, notifier, central HTML) — the worker on one server cannot see the others.
/// </summary>
public static class ScheduledTaskGroupEvaluator
{
    /// <summary>
    /// What the central reader knows about one server: its scheduled task snapshot, or that it is
    /// not reachable / its data is stale / its query failed (<see cref="Known"/> = false).
    /// </summary>
    public sealed record ServerSnapshot(string ServerId, string? Environment, bool Known, ScheduledTaskState? State);

    /// <summary>One server's view of a task inside a group.</summary>
    public sealed record GroupMember(string ServerId, bool Enabled, string State, ScheduledTaskInfo Task);

    /// <summary>
    /// The same task path in one environment. <see cref="UnknownServers"/> are servers of the environment
    /// with scheduled task monitoring enabled whose state is not known — they might have the task enabled.
    /// <see cref="AnchorServer"/> is the server the group problems are attached to: the alphabetically first participant of the environment from config.json (see <see cref="BuildGroups"/>).
    /// </summary>
    public sealed record TaskGroup(
        string Environment,
        string TaskPath,
        ScheduledTaskMode Mode,
        IReadOnlyList<GroupMember> Members,
        IReadOnlyList<string> UnknownServers,
        string AnchorServer)
    {
        /// <summary>Servers on which the task is enabled.</summary>
        public IReadOnlyList<string> ActiveServers => Members.Where(m => m.Enabled).Select(m => m.ServerId).ToList();
    }

    /// <summary>
    /// Builds the task groups for all environments. Servers without environment form the group "".
    /// Only servers with scheduled task monitoring enabled (global + per server) take part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Participants follow the worker gate (<see cref="IsParticipant"/>): feature enabled globally, server active,
    /// <c>checks.scheduledTasks.enabled</c> and at least one watch pattern — a server whose worker never queries
    /// tasks must not count as unknown forever. A participant counts as unknown when it has no snapshot,
    /// <see cref="ServerSnapshot.Known"/> is false, it reported no task state or its query failed — unknown is
    /// never treated as "disabled". Snapshots of servers that do not participate are ignored.
    /// </para>
    /// <para>
    /// Environments: <see cref="ServerSnapshot.Environment"/> when set, otherwise every environment of
    /// config.json that lists the server (a server in two environments takes part in both); none = "".
    /// </para>
    /// <para>
    /// Unknown rule (conservative, per environment): every unknown participant is listed in
    /// <see cref="TaskGroup.UnknownServers"/> of every group of its environment(s) — even for paths it never
    /// reported, because it might have exactly that task enabled. <see cref="DeriveGroupProblems"/> therefore
    /// never reports "none active" while a server of the environment is unknown.
    /// </para>
    /// <para>
    /// Mode: when servers report different modes for the same path, <see cref="ScheduledTaskMode.SingleActive"/>
    /// wins — a misconfigured pattern on one server must not silently switch the check off for the group.
    /// </para>
    /// <para>
    /// Anchor: the group problems are acknowledged (and tracked by the notifier) by (server ID, problem ID), so the
    /// anchor must not move while the finding persists — not even when a server times out for one read. WebUI,
    /// notifier and HTML generator share no persisted state, so the anchor is derived from config.json alone: the
    /// alphabetically first participant (<see cref="IsParticipant"/>, by <see cref="Server.Name"/>,
    /// OrdinalIgnoreCase) of the group's environment — independent of task state, membership and reachability.
    /// The anchor may therefore be a server that does not have the task or is currently unknown; the problem
    /// message names the affected servers, so it stays clear what it is about. The anchor changes only when the
    /// participants of the environment change in config.json.
    /// </para>
    /// Groups are ordered by environment and task path (OrdinalIgnoreCase); members by server ID.
    /// </remarks>
    public static IReadOnlyList<TaskGroup> BuildGroups(Config config, IEnumerable<ServerSnapshot> snapshots)
    {
        var servers = ResolveParticipants(config, snapshots);
        if (servers.Count == 0)
            return Array.Empty<TaskGroup>();

        var anchorByEnvironment = servers
            .GroupBy(s => s.Environment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First(),
                StringComparer.OrdinalIgnoreCase);

        var unknownByEnvironment = servers
            .Where(s => s.State == null)
            .GroupBy(s => s.Environment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(s => s.ServerId).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var members = servers
            .Where(s => s.State != null)
            .SelectMany(s => s.State!.Tasks
                .Where(t => !string.IsNullOrWhiteSpace(t.Path))
                .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => (s.Environment, Member: new GroupMember(s.ServerId, g.First().Enabled, g.First().State, g.First()))));

        return members
            .GroupBy(m => (Env: m.Environment.ToUpperInvariant(), Path: m.Member.Task.Path.ToUpperInvariant()))
            .Select(g =>
            {
                var ordered = g
                    .GroupBy(m => m.Member.ServerId, StringComparer.OrdinalIgnoreCase)
                    .Select(sg => sg.First())
                    .OrderBy(m => m.Member.ServerId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var groupMembers = ordered.Select(m => m.Member).ToList();
                var environment = ordered[0].Environment;
                var mode = groupMembers.Any(m => m.Task.Mode == ScheduledTaskMode.SingleActive)
                    ? ScheduledTaskMode.SingleActive
                    : ScheduledTaskMode.AllServers;
                var unknown = unknownByEnvironment.TryGetValue(environment, out var u) ? u : Array.Empty<string>();
                return new TaskGroup(environment, groupMembers[0].Task.Path, mode, groupMembers, unknown, anchorByEnvironment[environment]);
            })
            .OrderBy(g => g.Environment, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.TaskPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// (environment, server ID, config name, state or null = unknown) per participating server and environment.
    /// </summary>
    private static List<(string Environment, string ServerId, string Name, ScheduledTaskState? State)> ResolveParticipants(
        Config config, IEnumerable<ServerSnapshot> snapshots)
    {
        var features = config.GetEffectiveFeatures();
        var snapshotList = snapshots.ToList();
        var servers = new List<(string Environment, string ServerId, string Name, ScheduledTaskState? State)>();
        foreach (var server in config.Servers.Where(s => IsParticipant(features, s)))
        {
            var snapshot = snapshotList.FirstOrDefault(s => Matches(s.ServerId, server));
            var serverId = snapshot?.ServerId ?? server.Name;
            var known = snapshot is { Known: true, State: { QueryFailed: false } };
            var environments = snapshot?.Environment != null
                ? new List<string> { snapshot.Environment }
                : EnvironmentsOf(config, server.Name);
            foreach (var environment in environments)
                servers.Add((environment, serverId, server.Name, known ? snapshot!.State : null));
        }
        return servers;
    }

    /// <summary>
    /// Unknown participants per environment — also for environments in which no group can be built because
    /// every server that has a task is unknown. See <see cref="IsHeldByUnknownParticipants"/>.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> UnknownParticipantsByEnvironment(Config config, IEnumerable<ServerSnapshot> snapshots)
        => ResolveParticipants(config, snapshots)
            .Where(s => s.State == null)
            .GroupBy(s => s.Environment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(s => s.ServerId).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for a tracked group problem ID ("none active" / "several active") whose environment has at least one
    /// unknown participant. Covers the case "every member unknown → no group": readers holding state from earlier
    /// reads (notifier entries, first-seen store) keep such an entry pending instead of treating it as resolved.
    /// The environment is recognised by the ID prefix <c>schtask_&lt;env&gt;_</c> (conservative: a prefix that also
    /// matches a longer environment name only holds more, it never sends more).
    /// </summary>
    public static bool IsHeldByUnknownParticipants(string problemId, IReadOnlyDictionary<string, IReadOnlyList<string>> unknownByEnvironment)
    {
        if (!IsGroupProblemId(problemId))
            return false;

        foreach (var (environment, unknown) in unknownByEnvironment)
        {
            if (unknown.Count > 0 && problemId.StartsWith(GroupProblemIdPrefix(environment), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private const string NoneActiveSuffix = "_noneactive";
    private const string MultipleActiveSuffix = "_multiactive";

    private static bool IsGroupProblemId(string problemId)
        => problemId.StartsWith("schtask_", StringComparison.OrdinalIgnoreCase)
           && (problemId.EndsWith(NoneActiveSuffix, StringComparison.OrdinalIgnoreCase)
               || problemId.EndsWith(MultipleActiveSuffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>"schtask_&lt;env slug&gt;_" — derived from the factory so it follows its slug rules.</summary>
    private static string GroupProblemIdPrefix(string environment)
    {
        const string probePath = "p";
        var id = ActiveProblem.ScheduledTaskNoneActive(environment, probePath, Array.Empty<string>()).Id;
        return id[..^(probePath.Length + NoneActiveSuffix.Length)];
    }

    /// <summary>
    /// Same gate as the worker: the feature is enabled globally, the server is active, its scheduled task checks
    /// are enabled and it has at least one watch pattern.
    /// </summary>
    public static bool IsParticipant(FeaturesConfig features, Server server)
        => features.ScheduledTasks.Enabled
           && server.Active
           && !string.IsNullOrWhiteSpace(server.Name)
           && server.Checks?.ScheduledTasks is { Enabled: true } cfg
           && cfg.Tasks.Count > 0;

    /// <summary>Default worker interval when <c>general.checkIntervalSeconds</c> is not configured.</summary>
    internal static readonly TimeSpan DefaultWorkerInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Worst-case duration of one worker run on top of its interval: the CPU sampling window
    /// (<c>Metrics:Cpu:WindowMs</c>, default 60 s) runs after the checkers and the whole run is capped by
    /// <c>Metrics:Cpu:RuntimeBudgetSeconds</c> (default 240 s, incl. 30 s reserve). These host settings live in
    /// appsettings.json and are not visible to the central readers, so the default budget is used.
    /// </summary>
    internal static readonly TimeSpan WorkerRunReserve = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Age after which a server's status counts as stale (unknown) for the single-active check — shared by the
    /// WebUI, the notifier and the HTML generator so they agree on "none active".
    /// = max(<c>features.notifications.staleThresholdMinutes</c>, 3 × effective worker interval + run reserve).
    /// The effective interval is the larger of <c>general.checkIntervalSeconds</c> (default 60 s) and
    /// <c>features.scheduledTasks.checkIntervalSeconds</c>; three intervals tolerate two missed cycles, the
    /// reserve covers the run itself (<see cref="WorkerRunReserve"/>). Default: max(10 min, 3 × 60 s + 240 s) = 10 min.
    /// </summary>
    public static TimeSpan GetStaleAfter(Config config)
    {
        var features = config.GetEffectiveFeatures();
        var threshold = TimeSpan.FromMinutes(Math.Max(1, features.Notifications.StaleThresholdMinutes));

        var generalSeconds = config.General?.CheckIntervalSeconds ?? (int)DefaultWorkerInterval.TotalSeconds;
        var interval = TimeSpan.FromSeconds(Math.Max(1, Math.Max(generalSeconds, features.ScheduledTasks.CheckIntervalSeconds ?? 0)));
        var cycles = TimeSpan.FromTicks(interval.Ticks * 3) + WorkerRunReserve;

        return threshold > cycles ? threshold : cycles;
    }

    /// <summary>
    /// Derives the single-active problems (none active / several active) per anchor server ID.
    /// "None active" is only reported when no server of the group's environment is unknown.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ScheduledTaskMode.SingleActive"/> groups are checked. "Several active" is reported as
    /// soon as two known servers have the task enabled, even when other servers are unknown. Acknowledges and
    /// occurrences are applied by the caller (like for the per-server problems).
    /// </remarks>
    public static Dictionary<string, List<ActiveProblem>> DeriveGroupProblems(IReadOnlyList<TaskGroup> groups)
    {
        var result = new Dictionary<string, List<ActiveProblem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups.Where(g => g.Mode == ScheduledTaskMode.SingleActive))
        {
            var active = group.ActiveServers;
            ActiveProblem? problem = null;
            if (active.Count > 1)
                problem = ActiveProblem.ScheduledTaskMultipleActive(group.Environment, group.TaskPath, active);
            else if (active.Count == 0 && group.UnknownServers.Count == 0)
                problem = ActiveProblem.ScheduledTaskNoneActive(
                    group.Environment, group.TaskPath, group.Members.Select(m => m.ServerId).ToList());

            if (problem == null)
                continue;

            if (!result.TryGetValue(group.AnchorServer, out var list))
                result[group.AnchorServer] = list = new List<ActiveProblem>();
            list.Add(problem);
        }

        return result;
    }

    /// <summary>
    /// Group problem IDs per anchor that are "pending" (in der Schwebe): they cannot be decided because servers of
    /// the environment are unknown. With ≥ 1 unknown participant both IDs are pending — "none active" always (it is
    /// never reported while someone is unknown), "several active" unless ≥ 2 known servers are active (then it is
    /// certain and reported). They are not reported (see
    /// <see cref="DeriveGroupProblems"/>), but readers that track state across reads (notifier dedup, first-seen
    /// store) must treat them as still open — otherwise a server outage would yield a false "resolved" mail and,
    /// once the server is back, a second "new" mail with a reset start.
    /// </summary>
    public static Dictionary<string, HashSet<string>> DerivePendingGroupProblemIds(IReadOnlyList<TaskGroup> groups)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups.Where(g => g.Mode == ScheduledTaskMode.SingleActive && g.UnknownServers.Count > 0))
        {
            var active = group.ActiveServers;
            if (!result.TryGetValue(group.AnchorServer, out var ids))
                result[group.AnchorServer] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ids.Add(ActiveProblem.ScheduledTaskNoneActive(group.Environment, group.TaskPath, Array.Empty<string>()).Id);
            if (active.Count < 2)
                ids.Add(ActiveProblem.ScheduledTaskMultipleActive(group.Environment, group.TaskPath, active).Id);
        }

        return result;
    }

    /// <summary>
    /// Builds one snapshot per server from merged runtime statuses (several service files per server): the newest
    /// task query wins; a server is unknown when it reported no task state or its newest status is older than
    /// <paramref name="staleAfter"/> (e.g. the worker stopped). The environment is left to config.json.
    /// </summary>
    public static List<ServerSnapshot> SnapshotsFromStatuses(IEnumerable<RuntimeStatus> statuses, DateTime nowUtc, TimeSpan staleAfter)
        => statuses
            .Where(s => !string.IsNullOrWhiteSpace(s.Server))
            .GroupBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var state = g.Select(s => s.ScheduledTasks)
                    .Where(t => t != null)
                    .OrderByDescending(t => t!.LastChecked)
                    .FirstOrDefault();
                var fresh = nowUtc - g.Max(s => s.LastUpdate) <= staleAfter;
                return new ServerSnapshot(g.Key, null, state != null && fresh, state);
            })
            .ToList();

    private static List<string> EnvironmentsOf(Config config, string serverName)
    {
        var environments = config.Environments
            .Where(e => e.Value.GetAllServerNames().Contains(serverName, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Key)
            .ToList();
        return environments.Count > 0 ? environments : new List<string> { string.Empty };
    }

    private static bool Matches(string serverId, Server server)
        => string.Equals(serverId, server.Name, StringComparison.OrdinalIgnoreCase)
           || string.Equals(serverId, server.GetEffectiveId(), StringComparison.OrdinalIgnoreCase);
}
