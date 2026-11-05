using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Matching of Task Scheduler event ignore rules (<c>features.scheduledTasks.ignoreRules</c>). Used by the
/// worker's ScheduledTaskChecker and centrally by the WebUI and the notifier (same pattern as
/// <see cref="EventLogIgnoreMatcher"/>), so a rule created in the central WebUI works for all servers.
/// </summary>
public static class ScheduledTaskIgnoreMatcher
{
    /// <summary>
    /// True when the event matches an active rule. All set criteria (task path, event IDs, message) must
    /// match; expired rules and rules without any criterion are skipped. The server scope is NOT checked
    /// here — pre-filter with <see cref="RuleAppliesToServer"/>.
    /// </summary>
    public static bool IsIgnored(ScheduledTaskEvent ev, IReadOnlyList<ScheduledTaskIgnoreRule>? rules, DateTime nowUtc)
    {
        if (rules == null || rules.Count == 0)
            return false;

        foreach (var rule in rules)
        {
            if (rule.ExpiresAt.HasValue && rule.ExpiresAt.Value <= nowUtc)
                continue;

            var hasCriteria = !string.IsNullOrWhiteSpace(rule.TaskPath)
                              || rule.EventIds.Count > 0
                              || !string.IsNullOrWhiteSpace(rule.MessageContains);
            if (!hasCriteria)
                continue;

            if (!string.IsNullOrWhiteSpace(rule.TaskPath)
                && !EventLogIgnoreMatcher.MatchesWildcard(ScheduledTaskPaths.NormalizePath(ev.TaskPath), ScheduledTaskPaths.NormalizePattern(rule.TaskPath)))
                continue;

            if (rule.EventIds.Count > 0 && !rule.EventIds.Contains(ev.EventId))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.MessageContains))
            {
                var pattern = rule.MessageContains;
                var matches = pattern.Contains('*')
                    ? EventLogIgnoreMatcher.MatchesWildcard(ev.Message, pattern.StartsWith('*') || pattern.EndsWith('*') ? pattern : $"*{pattern}*")
                    : (ev.Message ?? string.Empty).Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>True when the rule applies to the server: empty list = all servers, else a wildcard match.</summary>
    public static bool RuleAppliesToServer(ScheduledTaskIgnoreRule rule, string serverName)
        => rule.Servers.Count == 0 || rule.Servers.Any(s => EventLogIgnoreMatcher.MatchesWildcard(serverName, s));

    /// <summary>
    /// Evaluates the event count against the thresholds (Healthy / Warning / Critical).
    /// </summary>
    public static MetricStatus EvaluateEventCount(int eventCount, ScheduledTaskDefaults? defaults)
    {
        var warning = Math.Max(1, defaults?.EventWarningCount ?? 1);
        var critical = Math.Max(warning, defaults?.EventCriticalCount ?? 5);
        return eventCount >= critical ? MetricStatus.Critical
            : eventCount >= warning ? MetricStatus.Warning
            : MetricStatus.Healthy;
    }

    /// <summary>
    /// Applies the rules centrally to a state reported by a server and re-evaluates
    /// <see cref="ScheduledTaskState.EventStatus"/> (never escalates). Returns the number of events removed.
    /// <para>
    /// With <see cref="ScheduledTaskState.EventGroups"/> (current worker) the rules are matched against the
    /// uncapped groups: task path and event ID of the group exactly, MessageContains against the group's
    /// <see cref="ScheduledTaskEventGroup.SampleMessage"/> (approximation — other runs of the group may have
    /// had other messages). <see cref="ScheduledTaskState.EventCount"/> becomes the sum of the remaining groups
    /// plus the events beyond the 200-group cap; <see cref="ScheduledTaskState.RecentEvents"/> drops exactly the
    /// entries of removed groups (list and count stay consistent). So a rule can silence a noisy task completely.
    /// </para>
    /// <para>
    /// Without groups (older worker) only the capped <see cref="ScheduledTaskState.RecentEvents"/> are known:
    /// matching entries are removed and EventCount is lowered by exactly that number.
    /// </para>
    /// </summary>
    public static int ApplyCentral(
        ScheduledTaskState? state,
        string serverName,
        IReadOnlyList<ScheduledTaskIgnoreRule>? rules,
        ScheduledTaskDefaults? defaults,
        DateTime nowUtc)
    {
        if (state == null || rules == null || rules.Count == 0)
            return 0;

        var serverRules = rules.Where(r => RuleAppliesToServer(r, serverName)).ToList();
        if (serverRules.Count == 0)
            return 0;

        int removed;
        if (state.EventGroups != null)
        {
            var untracked = Math.Max(0, state.EventCount - state.EventGroups.Sum(g => g.Count));
            var removedGroups = state.EventGroups.Where(g => IsIgnored(ToEvent(g), serverRules, nowUtc)).ToList();
            removed = removedGroups.Sum(g => g.Count);

            state.EventGroups.RemoveAll(removedGroups.Contains);
            // Only entries of removed groups leave the list, so list and count stay consistent (an entry whose
            // own message would match while its group's SampleMessage does not stays, as does its count).
            var removedEntries = state.RecentEvents.RemoveAll(e =>
                removedGroups.Any(g => g.EventId == e.EventId
                                       && ScheduledTaskPaths.NormalizePath(g.TaskPath).Equals(ScheduledTaskPaths.NormalizePath(e.TaskPath), StringComparison.OrdinalIgnoreCase)));
            if (removed == 0 && removedEntries == 0)
                return 0;

            state.EventCount = state.EventGroups.Sum(g => g.Count) + untracked;
        }
        else
        {
            if (state.RecentEvents.Count == 0)
                return 0;

            removed = state.RecentEvents.RemoveAll(e => IsIgnored(e, serverRules, nowUtc));
            if (removed == 0)
                return 0;

            // Only the removed entries are subtracted: RecentEvents is capped, so further (unreported)
            // events may still exist even when the list becomes empty.
            state.EventCount = Math.Max(0, state.EventCount - removed);
        }

        state.IgnoredEventCount += removed;

        var recomputed = EvaluateEventCount(state.EventCount, defaults);
        if (recomputed < state.EventStatus)
            state.EventStatus = recomputed;

        return removed;
    }

    private static ScheduledTaskEvent ToEvent(ScheduledTaskEventGroup group) => new()
    {
        TaskPath = group.TaskPath,
        EventId = group.EventId,
        Message = group.SampleMessage,
        Timestamp = group.LastSeen
    };
}
