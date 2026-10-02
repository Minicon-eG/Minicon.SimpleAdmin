using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Interface for checking Windows Event Log entries
/// </summary>
public interface IEventLogChecker
{
    /// <summary>
    /// Checks all configured Event Logs for a server.
    /// </summary>
    /// <param name="config">Per-server event log configuration</param>
    /// <param name="defaults">Global defaults (time window, levels, thresholds)</param>
    /// <param name="ignoreRules">
    /// Active ignore rules applicable to this server (already filtered by server scope).
    /// Matching events are excluded from the count thresholds.
    /// </param>
    Task<EventLogState?> CheckEventLogsAsync(
        EventLogChecksConfig config,
        EventLogDefaults defaults,
        IReadOnlyList<EventLogIgnoreRule>? ignoreRules = null);

    /// <summary>
    /// Checks a specific Event Log
    /// </summary>
    Task<EventLogState> CheckEventLogAsync(
        EventLogConfig config,
        int timeWindowMinutes,
        List<string> levels,
        int warningCount,
        int criticalCount,
        IReadOnlyList<EventLogIgnoreRule>? ignoreRules = null);
}
