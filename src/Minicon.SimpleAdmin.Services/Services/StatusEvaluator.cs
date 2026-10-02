using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Models.State;
using MetricStatus = Minicon.SimpleAdmin.Models.Status.MetricStatus;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Evaluates confirmed status based on historical data to prevent alert fatigue from temporary spikes
/// </summary>
public class StatusEvaluator : IStatusEvaluator
{
    private readonly ILogger<StatusEvaluator> _logger;

    /// <summary>
    /// Initializes a new instance of the StatusEvaluator class
    /// </summary>
    public StatusEvaluator(ILogger<StatusEvaluator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines overall service status considering acknowledges.
    /// If all problems are acknowledged, returns Acknowledged status.
    /// </summary>
    public ServiceStatus DetermineOverallStatusWithAcknowledges(List<Metric> metrics, List<ActiveProblem>? activeProblems)
    {
        // Check active problems first — they apply regardless of whether metrics exist
        if (activeProblems != null && activeProblems.Count > 0)
        {
            var unacknowledgedProblems = activeProblems.Where(p => !p.Acknowledged).ToList();

            // All problems acknowledged
            if (unacknowledgedProblems.Count == 0)
            {
                _logger.LogDebug("All {ProblemCount} problems are acknowledged - returning Acknowledged status",
                    activeProblems.Count);
                return ServiceStatus.Acknowledged;
            }

            // Some unacknowledged problems exist - return status based on worst unacknowledged
            var worstSeverity = unacknowledgedProblems.Max(p => p.Severity);
            if (worstSeverity == ProblemSeverity.Critical)
                return ServiceStatus.Unhealthy;
            if (worstSeverity == ProblemSeverity.Warning)
                return ServiceStatus.Degraded;
        }

        if (metrics.Count == 0)
        {
            return ServiceStatus.Unknown;
        }

        var hasCritical = metrics.Any(m => m.Status == MetricStatus.Critical);
        var hasWarning = metrics.Any(m => m.Status == MetricStatus.Warning);

        // No active problems tracked - use metric status
        if (hasCritical)
        {
            return ServiceStatus.Unhealthy;
        }

        if (hasWarning)
        {
            return ServiceStatus.Degraded;
        }

        return ServiceStatus.Healthy;
    }

    /// <summary>
    /// Evaluates the confirmed status for a metric based on historical values.
    /// Status changes are only confirmed after N consecutive checks show the same status.
    /// </summary>
    public MetricStatus GetConfirmedStatus(Metric currentMetric, List<MetricSnapshot> history, int minConsecutiveChecks)
    {
        if (currentMetric.Threshold == null)
        {
            return currentMetric.Status; // No threshold = use raw status
        }

        if (history.Count < minConsecutiveChecks)
        {
            _logger.LogDebug(
                "Not enough history for {MetricName}: {HistoryCount}/{MinChecks} - using current status {Status}",
                currentMetric.Name, history.Count, minConsecutiveChecks, currentMetric.Status);
            return currentMetric.Status; // Not enough history, use current
        }

        // Get last N values from history for this metric
        // History is sorted ascending (oldest first), so we need TakeLast to get the newest
        var recentStatuses = new List<MetricStatus>();

        foreach (var snapshot in history.TakeLast(minConsecutiveChecks))
        {
            if (snapshot.Values.TryGetValue(currentMetric.Name, out var historicalValue))
            {
                var status = EvaluateStatus(historicalValue, currentMetric.Threshold);
                recentStatuses.Add(status);
            }
            else
            {
                // Metric not found in this snapshot, can't evaluate
                _logger.LogDebug(
                    "Metric {MetricName} not found in snapshot from {Timestamp}",
                    currentMetric.Name, snapshot.Timestamp);
                return currentMetric.Status;
            }
        }

        // Check if all N checks have the same status
        var distinctStatuses = recentStatuses.Distinct().ToList();

        if (distinctStatuses.Count == 1)
        {
            var confirmedStatus = distinctStatuses[0];
            _logger.LogDebug(
                "Status confirmed for {MetricName}: {Status} (all {Count} checks consistent)",
                currentMetric.Name, confirmedStatus, minConsecutiveChecks);
            return confirmedStatus;
        }

        // Not all statuses are the same - return the least severe (most stable)
        // Priority: Ok < Warning < Critical
        var stableStatus = recentStatuses
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key) // Ok=0, Warning=1, Critical=2
            .First().Key;

        _logger.LogDebug(
            "Status not stable for {MetricName}: recent statuses [{Statuses}] - using {StableStatus}",
            currentMetric.Name,
            string.Join(", ", recentStatuses),
            stableStatus);

        return stableStatus;
    }

    /// <summary>
    /// Evaluates status for a single value against a threshold
    /// </summary>
    public MetricStatus EvaluateStatus(double value, MetricThreshold? threshold)
    {
        if (threshold == null)
        {
            return MetricStatus.Ok;
        }

        var op = threshold.Operator ?? ">";

        bool exceedsCritical = op switch
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

        bool exceedsWarning = op switch
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
}
