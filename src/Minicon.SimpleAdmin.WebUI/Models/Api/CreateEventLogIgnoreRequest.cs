using System.ComponentModel.DataAnnotations;

namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request payload for ignoring a single event (source + event ID) on one server.
/// </summary>
public class CreateEventLogIgnoreRequest
{
    [Required]
    public string ServerId { get; set; } = string.Empty;

    [Required]
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>End of the suppression (UTC). Null = permanent.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Why the event is ignored (stored in the rule for auditability).</summary>
    public string Reason { get; set; } = string.Empty;
}
