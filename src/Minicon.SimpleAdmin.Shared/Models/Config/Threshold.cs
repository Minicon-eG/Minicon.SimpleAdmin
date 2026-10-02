namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Threshold definition for metrics
/// </summary>
public class Threshold
{
    /// <summary>
    /// Gets or sets the warning threshold value
    /// </summary>
    public double? Warning { get; set; }

    /// <summary>
    /// Gets or sets the critical threshold value
    /// </summary>
    public double? Critical { get; set; }

    /// <summary>
    /// Gets or sets the comparison operator (e.g., ">", "<", ">=", "<=", "==", "!=")
    /// </summary>
    public string Operator { get; set; } = ">";
}
