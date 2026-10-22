namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// A DNS domain (host name) that is served by one or more servers of exactly one environment.
/// The environment is always set explicitly; the naming convention (<c>-dev</c>, <c>-ref</c>,
/// <c>-prod</c>, no suffix) only produces a suggestion and a warning, never the assignment itself.
/// Both production variants (<c>app</c> and <c>app-prod</c>) are independent entries.
/// </summary>
public class Domain
{
    /// <summary>
    /// Gets or sets the DNS name, e.g. <c>app-services-dev</c> or <c>app-services.example.org</c>
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the environment key (a key of <see cref="Config.Environments"/>)
    /// </summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the names of the servers that serve this domain
    /// </summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>
    /// Gets or sets the NetScaler vServer in front of this domain (optional, may be shared by several domains)
    /// </summary>
    public string? NetScalerVServer { get; set; }

    /// <summary>
    /// Gets or sets the name of a NetScaler health check (<see cref="HealthCheck.Name"/>) for this domain (optional)
    /// </summary>
    public string? HealthCheck { get; set; }

    /// <summary>
    /// Gets or sets an optional free-text description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether the domain may deviate from the naming convention of its environment
    /// (suppresses the mismatch warning)
    /// </summary>
    public bool AllowNameMismatch { get; set; }
}
