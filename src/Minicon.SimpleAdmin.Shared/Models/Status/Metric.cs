namespace Minicon.SimpleAdmin.Models.Status;

/// <summary>
/// Current metric value
/// </summary>
public class Metric
{
    /// <summary>
    /// Gets or sets the metric name (internal identifier)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name for the metric
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current metric value
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Gets or sets the unit of measurement (e.g., "%", "MB", "ms")
    /// </summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when this metric was collected
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the metric status (Ok, Warning, Critical)
    /// </summary>
    public MetricStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the threshold configuration for this metric
    /// </summary>
    public MetricThreshold? Threshold { get; set; }

    /// <summary>
    /// Gets or sets the average value of the last N measurements (minConsecutiveChecks).
    /// This value is used for status smoothing and represents the stabilized metric value.
    /// </summary>
    public double? AverageValue { get; set; }

    /// <summary>
    /// Gets or sets the number of measurements used to calculate the average.
    /// </summary>
    public int? AverageSampleCount { get; set; }

    /// <summary>
    /// Gets or sets the target identifier for this metric (e.g., drive letter for disk metrics)
    /// </summary>
    public string? Target { get; set; }
}
