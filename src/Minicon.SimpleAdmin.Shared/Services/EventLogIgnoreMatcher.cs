using System.Text.RegularExpressions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Matching of event log ignore rules (<c>features.eventLog.ignoreRules</c>). Used by the worker's
/// EventLogChecker (local, exact) and — since v2.25.0 — centrally by the WebUI and the notifier, so a
/// rule created in the central WebUI takes effect for every server without distributing config.json.
/// </summary>
public static class EventLogIgnoreMatcher
{
    /// <summary>
    /// True when the event matches an active ignore rule. All set criteria of a rule
    /// (source, event IDs, message, log name) must match; expired rules are skipped.
    /// </summary>
    public static bool IsIgnored(
        string source,
        int eventId,
        string message,
        string? logName,
        IReadOnlyList<EventLogIgnoreRule>? rules,
        DateTime nowUtc)
    {
        if (rules == null || rules.Count == 0)
            return false;

        foreach (var rule in rules)
        {
            // Expired rules no longer suppress — forces a review
            if (rule.ExpiresAt.HasValue && rule.ExpiresAt.Value <= nowUtc)
                continue;

            // A rule with no criteria would suppress everything — treat as inactive
            var hasCriteria = !string.IsNullOrWhiteSpace(rule.Source)
                              || rule.EventIds.Count > 0
                              || !string.IsNullOrWhiteSpace(rule.MessageContains);
            if (!hasCriteria)
                continue;

            // Log name is only checked when known (central entries carry no log name).
            if (!string.IsNullOrWhiteSpace(rule.LogName) && logName != null
                && !string.Equals(rule.LogName, logName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.Source) && !MatchesWildcard(source, rule.Source))
                continue;

            if (rule.EventIds.Count > 0 && !rule.EventIds.Contains(eventId))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.MessageContains))
            {
                var pattern = rule.MessageContains;
                var matches = pattern.Contains('*')
                    ? MatchesWildcard(message, pattern.StartsWith('*') || pattern.EndsWith('*') ? pattern : $"*{pattern}*")
                    : (message ?? string.Empty).Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the rule applies to the given server: empty server list = all servers,
    /// otherwise the hostname must match one entry (wildcard *, case-insensitive).
    /// </summary>
    public static bool RuleAppliesToServer(EventLogIgnoreRule rule, string serverName)
    {
        if (rule.Servers.Count == 0)
            return true;

        return rule.Servers.Any(s => MatchesWildcard(serverName, s));
    }

    /// <summary>
    /// Case-insensitive wildcard match: * = any characters, ? = single character.
    /// </summary>
    public static bool MatchesWildcard(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(value ?? string.Empty, regexPattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Applies the ignore rules centrally to an event log state reported by a server: removes matching
    /// entries from <see cref="EventLogState.RecentEvents"/>, lowers <see cref="EventLogState.EventCount"/>
    /// accordingly and re-evaluates the status against the global thresholds. Approximate by design —
    /// a server only reports its most recent events — but sufficient so that an event ignored in the
    /// central WebUI no longer raises a problem there or in the notifier mails.
    /// Returns the number of entries removed.
    /// </summary>
    public static int ApplyCentral(
        EventLogState? state,
        string serverName,
        IReadOnlyList<EventLogIgnoreRule>? rules,
        EventLogDefaults? defaults,
        DateTime nowUtc)
    {
        if (state == null || rules == null || rules.Count == 0 || state.RecentEvents.Count == 0)
            return 0;

        var serverRules = rules.Where(r => RuleAppliesToServer(r, serverName)).ToList();
        if (serverRules.Count == 0)
            return 0;

        var removed = state.RecentEvents.RemoveAll(e => IsIgnored(e.Source, e.EventId, e.Message, null, serverRules, nowUtc));
        if (removed == 0)
            return 0;

        state.IgnoredCount += removed;
        state.EventCount = state.RecentEvents.Count == 0 ? 0 : Math.Max(0, state.EventCount - removed);

        if (state.Status is MetricStatus.Warning or MetricStatus.Critical)
        {
            var warningCount = defaults?.WarningCount ?? 5;
            var criticalCount = defaults?.CriticalCount ?? 20;
            var recomputed = state.EventCount >= criticalCount ? MetricStatus.Critical
                : state.EventCount >= warningCount ? MetricStatus.Warning
                : MetricStatus.Healthy;
            // Never escalate — only lower what the server reported.
            if (recomputed < state.Status)
                state.Status = recomputed;
        }

        return removed;
    }
}
