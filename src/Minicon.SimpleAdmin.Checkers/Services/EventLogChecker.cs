using Minicon.SimpleAdmin.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

// Alias to avoid conflict with System.Diagnostics.EventLogEntry
using SharedEventLogEntry = Minicon.SimpleAdmin.Models.State.EventLogEntry;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks Windows Event Log entries
/// Falls back to empty results on non-Windows or when Event Log is unavailable
/// </summary>
public class EventLogChecker : IEventLogChecker
{
    private readonly ILogger<EventLogChecker> _logger;
    private readonly bool _isWindows;

    public EventLogChecker(ILogger<EventLogChecker> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Checks all configured Event Logs for a server
    /// </summary>
    public async Task<EventLogState?> CheckEventLogsAsync(
        EventLogChecksConfig config,
        EventLogDefaults defaults,
        IReadOnlyList<EventLogIgnoreRule>? ignoreRules = null)
    {
        if (!_isWindows)
        {
            _logger.LogDebug("Not running on Windows, returning empty Event Log results");
            return null;
        }

        if (!config.Enabled)
        {
            return null;
        }

        // Use default logs when none are explicitly configured
        var effectiveConfig = config.Logs.Count > 0
            ? config
            : new EventLogChecksConfig
            {
                Enabled = true,
                Logs = new List<EventLogConfig>
                {
                    new EventLogConfig { Name = "Application" },
                    new EventLogConfig { Name = "System" }
                }
            };

        return await CheckEventLogsInternalAsync(effectiveConfig, defaults, ignoreRules);
    }

    /// <summary>
    /// Internal method to check multiple event logs
    /// </summary>
    private async Task<EventLogState> CheckEventLogsInternalAsync(
        EventLogChecksConfig config,
        EventLogDefaults defaults,
        IReadOnlyList<EventLogIgnoreRule>? ignoreRules)
    {
        var combinedState = new EventLogState
        {
            LastChecked = DateTime.UtcNow,
            RecentEvents = new List<SharedEventLogEntry>()
        };

        var totalEventCount = 0;
        var totalIgnoredCount = 0;
        var worstStatus = MetricStatus.Healthy;

        foreach (var logConfig in config.Logs)
        {
            try
            {
                var effectiveTimeWindow = logConfig.TimeWindowMinutes ?? defaults.TimeWindowMinutes;
                var effectiveLevels = logConfig.Levels ?? defaults.Levels;
                var effectiveWarningCount = logConfig.WarningCount ?? defaults.WarningCount;
                var effectiveCriticalCount = logConfig.CriticalCount ?? defaults.CriticalCount;

                var logState = await CheckEventLogAsync(
                    logConfig,
                    effectiveTimeWindow,
                    effectiveLevels,
                    effectiveWarningCount,
                    effectiveCriticalCount,
                    ignoreRules);

                totalEventCount += logState.EventCount;
                totalIgnoredCount += logState.IgnoredCount;

                if (logState.Status > worstStatus)
                {
                    worstStatus = logState.Status;
                }

                // Add recent events to combined state (limit total)
                combinedState.RecentEvents.AddRange(logState.RecentEvents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Event Log {LogName}", logConfig.Name);
            }
        }

        // Sort events by timestamp descending and limit
        combinedState.RecentEvents = combinedState.RecentEvents
            .OrderByDescending(e => e.Timestamp)
            .Take(50)
            .ToList();

        combinedState.EventCount = totalEventCount;
        combinedState.IgnoredCount = totalIgnoredCount;
        combinedState.Status = worstStatus;

        return combinedState;
    }

    /// <summary>
    /// Checks a specific Event Log
    /// </summary>
    public async Task<EventLogState> CheckEventLogAsync(
        EventLogConfig config,
        int timeWindowMinutes,
        List<string> levels,
        int warningCount,
        int criticalCount,
        IReadOnlyList<EventLogIgnoreRule>? ignoreRules = null)
    {
        var state = new EventLogState
        {
            LastChecked = DateTime.UtcNow,
            RecentEvents = new List<SharedEventLogEntry>()
        };

        if (!_isWindows)
        {
            state.Status = MetricStatus.Unknown;
            return state;
        }

        try
        {
            // Use Get-WinEvent via PowerShell for more flexibility
            var events = await GetEventsAsync(config.Name, timeWindowMinutes, levels);

            // Per-log source filters (include/exclude with wildcards)
            var sourceFiltered = events
                .Where(e => MatchesSourceFilters(e.Source, config.IncludeSources, config.ExcludeSources))
                .ToList();

            // Central ignore rules: suppress known noise, count separately for transparency
            var now = DateTime.UtcNow;
            var remaining = new List<EventInfo>();
            var ignored = 0;
            foreach (var e in sourceFiltered)
            {
                if (IsEventIgnored(e.Source, e.Id, e.Message, config.Name, ignoreRules, now))
                    ignored++;
                else
                    remaining.Add(e);
            }

            state.EventCount = remaining.Count;
            state.IgnoredCount = ignored;

            // Determine status based on event count (ignored events do not count)
            if (remaining.Count >= criticalCount)
            {
                state.Status = MetricStatus.Critical;
            }
            else if (remaining.Count >= warningCount)
            {
                state.Status = MetricStatus.Warning;
            }
            else
            {
                state.Status = MetricStatus.Healthy;
            }

            // Map events to SharedEventLogEntry
            state.RecentEvents = remaining.Take(20).Select(e => new SharedEventLogEntry
            {
                Timestamp = e.Timestamp,
                Level = e.Level,
                Source = e.Source,
                Message = e.Message,
                EventId = e.Id
            }).ToList();

            _logger.LogDebug("Event Log {LogName}: {Count} events ({Ignored} ignored), status: {Status}",
                config.Name, remaining.Count, ignored, state.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Event Log {LogName}", config.Name);
            state.Status = MetricStatus.Unknown;
        }

        return state;
    }

    /// <summary>
    /// Applies the per-log include/exclude source patterns (wildcard *, case-insensitive).
    /// Include list empty/null = all sources pass; exclude wins over include.
    /// </summary>
    internal static bool MatchesSourceFilters(string source, List<string>? includeSources, List<string>? excludeSources)
    {
        if (excludeSources is { Count: > 0 } && excludeSources.Any(p => MatchesWildcard(source, p)))
            return false;

        if (includeSources is { Count: > 0 })
            return includeSources.Any(p => MatchesWildcard(source, p));

        return true;
    }

    /// <summary>
    /// True when the event matches an active ignore rule. All set criteria of a rule
    /// (source, event IDs, message, log name) must match; expired rules are skipped.
    /// </summary>
    internal static bool IsEventIgnored(
        string source,
        int eventId,
        string message,
        string logName,
        IReadOnlyList<EventLogIgnoreRule>? rules,
        DateTime nowUtc)
        => EventLogIgnoreMatcher.IsIgnored(source, eventId, message, logName, rules, nowUtc);

    /// <summary>
    /// True when the rule applies to the given server: empty server list = all servers,
    /// otherwise the hostname must match one entry (wildcard *, case-insensitive).
    /// </summary>
    public static bool RuleAppliesToServer(EventLogIgnoreRule rule, string serverName)
        => EventLogIgnoreMatcher.RuleAppliesToServer(rule, serverName);

    /// <summary>
    /// Case-insensitive wildcard match: * = any characters, ? = single character.
    /// </summary>
    internal static bool MatchesWildcard(string value, string pattern)
        => EventLogIgnoreMatcher.MatchesWildcard(value, pattern);

    /// <summary>
    /// Gets events from a specific Event Log using PowerShell
    /// </summary>
    private async Task<List<EventInfo>> GetEventsAsync(string logName, int timeWindowMinutes, List<string> levels)
    {
        var events = new List<EventInfo>();

        try
        {
            // Map level names to numbers
            var levelFilter = string.Join(",", levels.Select(l => l switch
            {
                "Critical" => "1",
                "Error" => "2",
                "Warning" => "3",
                "Information" => "4",
                _ => "2" // Default to Error
            }));

            // Build filter hash table for Get-WinEvent — Level must be inside the hashtable,
            // not as a standalone -Level parameter (which Get-WinEvent does not support)
            var filterHashTable = $"@{{LogName='{logName}';StartTime=(Get-Date).AddMinutes(-{timeWindowMinutes});Level=@({levelFilter})}}";

            var script = $@"
$events = Get-WinEvent -FilterHashtable {filterHashTable} -ErrorAction SilentlyContinue | Select-Object -First 500
$events | ForEach-Object {{
    [PSCustomObject]@{{
        TimeCreated = $_.TimeCreated.ToString('o')
        Level = $_.LevelDisplayName
        ProviderName = $_.ProviderName
        Message = $_.Message
        Id = $_.Id
    }}
}} | ConvertTo-Json -Compress
";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return events;
            }

            var output = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
            process.WaitForExitAsync().GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(output))
            {
                return events;
            }

            // ConvertTo-Json returns an array for multiple results and an object for a single result.
            using var document = JsonDocument.Parse(output);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var jsonResults = JsonSerializer.Deserialize<List<EventInfo>>(output, options);
                if (jsonResults != null)
                {
                    events = jsonResults;
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var singleEvent = JsonSerializer.Deserialize<EventInfo>(output, options);
                if (singleEvent != null)
                {
                    events.Add(singleEvent);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting events from {LogName}", logName);
        }

        return events;
    }

    /// <summary>
    /// Helper class for JSON deserialization of event data
    /// </summary>
    private class EventInfo
    {
        public string TimeCreated { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Id { get; set; }

        public DateTime Timestamp => DateTime.TryParse(TimeCreated, out var dt) ? dt : DateTime.MinValue;
        public string Source => ProviderName;
    }
}
