namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// PRTG monitoring configuration
/// </summary>
public class PrtgConfig
{
    /// <summary>
    /// Gets or sets whether PRTG monitoring is enabled
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// Gets or sets the admin endpoint path (e.g., "/admin/service/monitor")
    /// </summary>
    public string AdminPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the list of PRTG checks to perform
    /// </summary>
    public List<PrtgCheck> Checks { get; set; } = new();
}
