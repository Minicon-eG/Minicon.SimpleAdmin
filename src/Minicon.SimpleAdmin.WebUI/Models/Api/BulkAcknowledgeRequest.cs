using System.ComponentModel.DataAnnotations;

namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request payload for acknowledging several problems at once with shared settings.
/// </summary>
public class BulkAcknowledgeRequest
{
    [Required]
    public List<BulkAcknowledgeItem> Items { get; set; } = new();

    [Required]
    public string AcknowledgedBy { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int DurationMinutes { get; set; }

    public bool AutoResetOnHealthy { get; set; } = true;

    public bool SuppressAlerts { get; set; } = true;

    /// <summary>
    /// Optional end date ("bis Datum"). Wins over <see cref="DurationMinutes"/>; must lie in the future.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// A single problem (server + problem id) within a <see cref="BulkAcknowledgeRequest"/>.
/// </summary>
public class BulkAcknowledgeItem
{
    [Required]
    public string ServerId { get; set; } = string.Empty;

    [Required]
    public string ProblemId { get; set; } = string.Empty;
}
