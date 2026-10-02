namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Historical metric snapshot
/// </summary>
public class MetricSnapshot
{
    /// <summary>
    /// Gets or sets the timestamp when this snapshot was taken
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the metric values at this point in time (metric name -> value)
    /// </summary>
    public Dictionary<string, double> Values { get; set; } = new();
}
