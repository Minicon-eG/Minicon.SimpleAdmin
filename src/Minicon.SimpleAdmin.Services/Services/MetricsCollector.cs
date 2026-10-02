using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services.PlatformMetrics;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Platform-agnostic metrics collector that orchestrates metric collection
/// </summary>
public class MetricsCollector : IMetricsCollector
{
    private readonly IPlatformMetricsProvider _platformProvider;
    private readonly ILogger<MetricsCollector> _logger;

    /// <summary>
    /// Initializes a new instance of the MetricsCollector class
    /// </summary>
    public MetricsCollector(
        IPlatformMetricsProvider platformProvider,
        ILogger<MetricsCollector> logger)
    {
        _platformProvider = platformProvider;
        _logger = logger;
    }

    /// <summary>
    /// Collects current metrics from the system
    /// </summary>
    public async Task<List<Metric>> CollectMetricsAsync(List<PrtgCheck> checks)
    {
        _logger.LogDebug("Collecting {CheckCount} metric(s)", checks.Count);

        var metrics = new List<Metric>();
        var timestamp = DateTime.UtcNow;

        foreach (var check in checks)
        {
            try
            {
                var metric = await CollectMetricAsync(check, timestamp);
                if (metric != null)
                {
                    metrics.Add(metric);
                    _logger.LogDebug("Collected metric '{MetricName}': {Value}{Unit} ({Status})",
                        metric.Name, metric.Value, metric.Unit, metric.Status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting metric '{MetricName}'", check.Metric);
            }
        }

        _logger.LogInformation("Collected {MetricCount} metric(s)", metrics.Count);
        return metrics;
    }

    /// <summary>
    /// Collects a single metric
    /// </summary>
    private async Task<Metric?> CollectMetricAsync(PrtgCheck check, DateTime timestamp)
    {
        double value = 0;
        string unit = "%";

        switch (check.Metric.ToLowerInvariant())
        {
            case "cpu":
                value = await _platformProvider.GetCpuUsageAsync();
                unit = "%";
                break;

            case "memory":
                value = await _platformProvider.GetMemoryUsageAsync();
                unit = "%";
                break;

            case "disk":
                value = await _platformProvider.GetDiskFreeSpaceAsync(check.Target ?? "C:");
                unit = "%";
                break;

            default:
                _logger.LogWarning("Unknown metric type: {MetricType}", check.Metric);
                return null;
        }

        var status = DetermineStatus(value, check.Threshold);

        return new Metric
        {
            Name = check.Metric.ToLowerInvariant(),
            DisplayName = check.DisplayName ?? check.Metric,
            Value = Math.Round(value, 2),
            Unit = unit,
            Timestamp = timestamp,
            Status = status,
            Target = check.Target,
            Threshold = check.Threshold != null
                ? new MetricThreshold
                {
                    Warning = check.Threshold.Warning,
                    Critical = check.Threshold.Critical,
                    Operator = check.Threshold.Operator
                }
                : null
        };
    }

    /// <summary>
    /// Determines metric status based on threshold
    /// </summary>
    private MetricStatus DetermineStatus(double value, Threshold? threshold)
    {
        if (threshold == null)
        {
            return MetricStatus.Ok;
        }

        bool exceedsCritical = threshold.Operator switch
        {
            ">" => threshold.Critical.HasValue && value > threshold.Critical.Value,
            "<" => threshold.Critical.HasValue && value < threshold.Critical.Value,
            ">=" => threshold.Critical.HasValue && value >= threshold.Critical.Value,
            "<=" => threshold.Critical.HasValue && value <= threshold.Critical.Value,
            "==" => threshold.Critical.HasValue && Math.Abs(value - threshold.Critical.Value) < 0.01,
            "!=" => threshold.Critical.HasValue && Math.Abs(value - threshold.Critical.Value) >= 0.01,
            _ => false
        };

        if (exceedsCritical)
        {
            return MetricStatus.Critical;
        }

        bool exceedsWarning = threshold.Operator switch
        {
            ">" => threshold.Warning.HasValue && value > threshold.Warning.Value,
            "<" => threshold.Warning.HasValue && value < threshold.Warning.Value,
            ">=" => threshold.Warning.HasValue && value >= threshold.Warning.Value,
            "<=" => threshold.Warning.HasValue && value <= threshold.Warning.Value,
            "==" => threshold.Warning.HasValue && Math.Abs(value - threshold.Warning.Value) < 0.01,
            "!=" => threshold.Warning.HasValue && Math.Abs(value - threshold.Warning.Value) >= 0.01,
            _ => false
        };

        return exceedsWarning ? MetricStatus.Warning : MetricStatus.Ok;
    }

    /// <summary>
    /// Disposes resources
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebug("Disposing MetricsCollector");
        _platformProvider?.Dispose();
    }
}
