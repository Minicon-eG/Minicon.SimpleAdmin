namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Status data for a single server, used for central aggregation
/// </summary>
public class CentralServerStatus
{
    /// <summary>
    /// Gets or sets the hostname of the server
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this status was generated
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Gets or sets the list of service statuses on this server
    /// </summary>
    public List<RuntimeStatus> Statuses { get; set; } = new();
}
