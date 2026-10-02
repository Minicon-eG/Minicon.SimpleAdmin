namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Problem acknowledgement feature configuration
/// </summary>
public class AcknowledgeFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default duration in minutes for new acknowledges (default: 60)
    /// </summary>
    public int DefaultDurationMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum allowed duration in hours (default: 24)
    /// </summary>
    public int MaxDurationHours { get; set; } = 24;

    /// <summary>
    /// Whether "until manually reset" option is allowed (default: false)
    /// </summary>
    public bool AllowIndefinite { get; set; } = false;

    /// <summary>
    /// Whether a comment is required for acknowledging (default: true)
    /// </summary>
    public bool RequireComment { get; set; } = true;

    /// <summary>
    /// Whether to auto-reset acknowledge when problem is resolved (default: true)
    /// </summary>
    public bool AutoResetOnHealthy { get; set; } = true;

    /// <summary>
    /// Whether to notify team when a problem is acknowledged (default: true)
    /// </summary>
    public bool NotifyTeamOnAck { get; set; } = true;

    /// <summary>
    /// Whether to keep acknowledge history (default: true)
    /// </summary>
    public bool LogHistory { get; set; } = true;

    /// <summary>
    /// Number of days to retain acknowledge history (default: 90)
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>
    /// Available duration options for the acknowledge dialog
    /// </summary>
    public List<AcknowledgeDurationOption> Durations { get; set; } = new()
    {
        new() { Label = "30 Minuten", Minutes = 30 },
        new() { Label = "1 Stunde", Minutes = 60 },
        new() { Label = "2 Stunden", Minutes = 120 },
        new() { Label = "4 Stunden", Minutes = 240 },
        new() { Label = "8 Stunden", Minutes = 480 },
        new() { Label = "24 Stunden", Minutes = 1440 }
    };

    /// <summary>
    /// Timeout behavior configuration
    /// </summary>
    public AcknowledgeTimeoutBehavior TimeoutBehavior { get; set; } = new();
}

/// <summary>
/// Duration option for acknowledge dialog
/// </summary>
public class AcknowledgeDurationOption
{
    /// <summary>
    /// Display label (e.g., "1 Stunde")
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Duration in minutes
    /// </summary>
    public int Minutes { get; set; }
}

/// <summary>
/// Configuration for acknowledge timeout behavior
/// </summary>
public class AcknowledgeTimeoutBehavior
{
    /// <summary>
    /// Whether to re-alert when acknowledge times out and problem still exists (default: true)
    /// </summary>
    public bool ReAlert { get; set; } = true;

    /// <summary>
    /// Whether to escalate after repeated timeouts (default: false)
    /// </summary>
    public bool Escalate { get; set; } = false;

    /// <summary>
    /// Number of timeouts before escalation (default: 2)
    /// </summary>
    public int EscalateAfterTimeouts { get; set; } = 2;
}
