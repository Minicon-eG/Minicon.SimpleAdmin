using System.ComponentModel.DataAnnotations;

namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request payload for extending an acknowledge duration.
/// </summary>
public class ExtendAcknowledgeRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int AdditionalMinutes { get; set; }
}
