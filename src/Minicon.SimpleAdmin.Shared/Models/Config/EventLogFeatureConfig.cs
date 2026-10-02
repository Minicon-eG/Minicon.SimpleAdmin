using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Windows Event Log feature configuration
/// </summary>
public class EventLogFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: false - opt-in)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional: Custom check interval in seconds (null = use global interval)
    /// </summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>
    /// Default settings for event log monitoring
    /// </summary>
    public EventLogDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Central ignore rules: events matching one of these rules are excluded from the
    /// count thresholds (suppressing known noise long-term). Each rule can be scoped
    /// to all servers (empty <see cref="EventLogIgnoreRule.Servers"/>) or specific
    /// servers (names, wildcards supported).
    /// </summary>
    public List<EventLogIgnoreRule> IgnoreRules { get; set; } = new();
}

/// <summary>
/// One long-term ignore rule for event log monitoring. An event is ignored when ALL
/// set criteria match (source, event IDs, message substring, log name) AND the rule
/// applies to the checking server AND the rule has not expired.
/// </summary>
public class EventLogIgnoreRule
{
    /// <summary>
    /// Event source / provider name to match (wildcard * supported, case-insensitive).
    /// Null or empty = any source.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Event IDs to match. Empty = any event ID.
    /// </summary>
    public List<int> EventIds { get; set; } = new();

    /// <summary>
    /// Case-insensitive substring (or wildcard pattern) the event message must contain.
    /// Null or empty = any message.
    /// </summary>
    public string? MessageContains { get; set; }

    /// <summary>
    /// Event log name this rule is limited to (e.g. "Application"). Null or empty = all logs.
    /// </summary>
    public string? LogName { get; set; }

    /// <summary>
    /// Server names this rule applies to (wildcard * supported, case-insensitive).
    /// Empty = all servers.
    /// </summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>
    /// Why this rule exists (owner/context). Strongly recommended so suppressions
    /// stay auditable.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional expiry (UTC). After this instant the rule no longer suppresses events,
    /// forcing a review. Null = permanent.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Form-binding helper: comma-separated representation of <see cref="EventIds"/>.
    /// Not serialized to config.json.
    /// </summary>
    [JsonIgnore]
    public string EventIdsText
    {
        get => string.Join(",", EventIds);
        set => EventIds = (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    /// <summary>
    /// Form-binding helper: comma-separated representation of <see cref="Servers"/>.
    /// Not serialized to config.json.
    /// </summary>
    [JsonIgnore]
    public string ServersText
    {
        get => string.Join(", ", Servers);
        set => Servers = (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// Default settings for Event Log monitoring
/// </summary>
public class EventLogDefaults
{
    /// <summary>
    /// Time window in minutes to look back for events (default: 60)
    /// </summary>
    public int TimeWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Event levels to monitor (default: Critical, Error)
    /// </summary>
    public List<string> Levels { get; set; } = new() { "Critical", "Error" };

    /// <summary>
    /// Number of events in timeframe that triggers warning (default: 5)
    /// </summary>
    public int WarningCount { get; set; } = 5;

    /// <summary>
    /// Number of events in timeframe that triggers critical (default: 20)
    /// </summary>
    public int CriticalCount { get; set; } = 20;
}

/// <summary>
/// Server-specific Event Log checks configuration
/// </summary>
public class EventLogChecksConfig
{
    /// <summary>
    /// Whether Event Log checks are enabled for this server (default: false)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// List of Event Logs to monitor on this server
    /// </summary>
    public List<EventLogConfig> Logs { get; set; } = new();
}

/// <summary>
/// Configuration for a specific Event Log to monitor
/// </summary>
public class EventLogConfig
{
    /// <summary>
    /// The Event Log name (e.g., "Application", "System")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Event levels to monitor (overrides global default)
    /// </summary>
    public List<string>? Levels { get; set; }

    /// <summary>
    /// Source patterns to include (wildcard * supported)
    /// </summary>
    public List<string>? IncludeSources { get; set; }

    /// <summary>
    /// Source patterns to exclude (wildcard * supported)
    /// </summary>
    public List<string>? ExcludeSources { get; set; }

    /// <summary>
    /// Time window in minutes (overrides global default)
    /// </summary>
    public int? TimeWindowMinutes { get; set; }

    /// <summary>
    /// Warning count threshold (overrides global default)
    /// </summary>
    public int? WarningCount { get; set; }

    /// <summary>
    /// Critical count threshold (overrides global default)
    /// </summary>
    public int? CriticalCount { get; set; }
}
