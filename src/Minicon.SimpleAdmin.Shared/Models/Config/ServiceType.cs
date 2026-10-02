using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Service type definitions
/// </summary>
public class ServiceType
{
    private NetScalerConfig? _netScaler;
    private PrtgConfig? _prtg;

    /// <summary>
    /// Gets or sets the service type name (e.g., "payment-core", "reporting")
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the NetScaler load balancer configuration.
    /// Returns a default instance if not configured (never null).
    /// </summary>
    [JsonPropertyName("netscaler")]
    public NetScalerConfig NetScaler
    {
        get => _netScaler ??= new NetScalerConfig();
        set => _netScaler = value;
    }
    
    /// <summary>
    /// Gets or sets the PRTG monitoring configuration.
    /// Returns a default instance if not configured (never null).
    /// </summary>
    public PrtgConfig Prtg
    {
        get => _prtg ??= new PrtgConfig();
        set => _prtg = value;
    }
}
