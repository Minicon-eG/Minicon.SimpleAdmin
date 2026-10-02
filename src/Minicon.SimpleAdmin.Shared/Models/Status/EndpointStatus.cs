namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Endpoint status information
/// </summary>
public class EndpointStatus
{
    /// <summary>
    /// Gets or sets the endpoint type (PRTG or NetScaler)
    /// </summary>
    public EndpointType Type { get; set; }

    /// <summary>
    /// Gets or sets the endpoint path
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this endpoint is enabled
    /// </summary>
    public bool Enabled { get; set; }
}
