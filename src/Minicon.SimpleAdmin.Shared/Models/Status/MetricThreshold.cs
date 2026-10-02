namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Metric threshold configuration
/// </summary>
public class MetricThreshold
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
