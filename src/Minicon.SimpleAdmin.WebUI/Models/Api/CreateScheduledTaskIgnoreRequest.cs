namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request payload for ignoring a Task Scheduler event (task path + event ID) on one server.
/// The duration is either <see cref="DurationDays"/> (1, 7, 30 or 90), an explicit <see cref="ExpiresAt"/>
/// or neither (permanent).
/// </summary>
public class CreateScheduledTaskIgnoreRequest
{
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Task path of the event (empty = any task, then <see cref="EventId"/> is required).</summary>
    public string? TaskPath { get; set; }

    public int EventId { get; set; }

    /// <summary>Suppression period in days (1, 7, 30, 90). Null = use <see cref="ExpiresAt"/> or permanent.</summary>
    public int? DurationDays { get; set; }

    /// <summary>End of the suppression (UTC). Null = permanent (unless <see cref="DurationDays"/> is set).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Why the event is ignored (required, stored in the rule for auditability).</summary>
    public string Reason { get; set; } = string.Empty;
}
