namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// NetScaler load balancer configuration
/// </summary>
public class NetScalerConfig
{
    /// <summary>
    /// Gets or sets whether NetScaler health checks are enabled
    /// </summary>
    public bool Enabled { get; set; } = false;
    
    /// <summary>
    /// Gets or sets the admin endpoint path (e.g., "/admin/service/loadbalancer")
    /// </summary>
    public string AdminPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the list of health checks to perform
    /// </summary>
    public List<HealthCheck> HealthChecks { get; set; } = new();
}
