namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Output settings for generated HTML pages
/// </summary>
public class OutputSettings
{
    /// <summary>
    /// Gets or sets the OK marker text that is included in healthy status pages
    /// This marker is used by LoadBalancers and PRTG to verify service health
    /// </summary>
    /// <example>##OK##</example>
    public string OkMarker { get; set; } = "##OK##";

    /// <summary>
    /// Gets or sets the central output path where status JSON files are copied to.
    /// This allows aggregating status from multiple servers in a central location.
    /// Supports {hostname} placeholder which will be replaced with the current machine name.
    /// </summary>
    /// <example>\\\\fileserver\\status</example>
    /// <example>D:\\CentralStatus</example>
    public string? CentralOutputPath { get; set; }

    /// <summary>
    /// Gets or sets the central generator settings.
    /// Enable this on ONE instance to generate aggregated HTML from all server statuses.
    /// </summary>
    public CentralGeneratorSettings? CentralGenerator { get; set; }

    /// <summary>
    /// WebUI: additionally fetch the status of every active server over HTTP from
    /// "{baseUrl}/status/{name}.status.json" (the file each worker publishes into its wwwroot) and merge
    /// it with the status files. Lets one central WebUI show all servers without a shared folder.
    /// Servers that answer neither via HTTP nor via a status file are shown as "nicht erreichbar"
    /// (default: true).
    /// </summary>
    public bool PullStatusOverHttp { get; set; } = true;
}
