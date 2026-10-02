namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Health check definition for NetScaler
/// </summary>
public class HealthCheck
{
    /// <summary>
    /// Gets or sets the name of the health check
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the HTTP method (GET, POST, HEAD, PUT, DELETE)
    /// </summary>
    public string Method { get; set; } = "GET";
    
    /// <summary>
    /// Gets or sets the relative path for the health check
    /// </summary>
    public string Path { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the optional request payload
    /// </summary>
    public string? Payload { get; set; }
    
    /// <summary>
    /// Gets or sets the expected response criteria
    /// </summary>
    public ExpectedResponse Expected { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the interval in seconds between checks
    /// </summary>
    public int? IntervalSeconds { get; set; }
    
    /// <summary>
    /// Gets or sets the timeout in milliseconds for the health check
    /// </summary>
    public int? TimeoutMs { get; set; }
}
