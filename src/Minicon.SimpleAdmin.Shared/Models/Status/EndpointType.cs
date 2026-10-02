namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Type of endpoint
/// </summary>
public enum EndpointType
{
    /// <summary>
    /// PRTG monitoring endpoint
    /// </summary>
    Prtg,

    /// <summary>
    /// NetScaler load balancer health check endpoint
    /// </summary>
    NetScaler
}
