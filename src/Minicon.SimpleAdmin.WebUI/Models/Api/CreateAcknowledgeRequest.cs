using System.ComponentModel.DataAnnotations;

namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request payload for creating an acknowledge entry.
/// </summary>
public class CreateAcknowledgeRequest
{
    [Required]
    public string ServerId { get; set; } = string.Empty;

    [Required]
    public string ProblemId { get; set; } = string.Empty;

    [Required]
    public string AcknowledgedBy { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int DurationMinutes { get; set; }

    public bool AutoResetOnHealthy { get; set; } = true;

    public bool SuppressAlerts { get; set; } = true;

    /// <summary>
    /// Optional end date ("bis Datum"). Wins over <see cref="DurationMinutes"/>; must lie in the future.
    /// Neither set (DurationMinutes = 0, no ExpiresAt) = indefinite until cancelled manually.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
