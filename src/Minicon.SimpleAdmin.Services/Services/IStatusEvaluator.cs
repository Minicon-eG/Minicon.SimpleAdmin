using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Models.State;
using MetricStatus = Minicon.SimpleAdmin.Models.Status.MetricStatus;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Interface for evaluating confirmed status based on historical data
/// </summary>
public interface IStatusEvaluator
{
    /// <summary>
    /// Evaluates the confirmed status for a metric based on historical values.
    /// Status changes are only confirmed after N consecutive checks show the same status.
    /// </summary>
    /// <param name="currentMetric">The current metric with threshold configuration</param>
    /// <param name="history">Historical metric snapshots (most recent first)</param>
    /// <param name="minConsecutiveChecks">Minimum consecutive checks required to confirm a status change</param>
    /// <returns>The confirmed status</returns>
    MetricStatus GetConfirmedStatus(Metric currentMetric, List<MetricSnapshot> history, int minConsecutiveChecks);

    /// <summary>
    /// Evaluates status for a single value against a threshold
    /// </summary>
    /// <param name="value">The metric value</param>
    /// <param name="threshold">The threshold configuration</param>
    /// <returns>The evaluated status</returns>
    MetricStatus EvaluateStatus(double value, MetricThreshold? threshold);

    /// <summary>
    /// Determines overall service status considering acknowledges
    /// </summary>
    /// <param name="metrics">The current metrics</param>
    /// <param name="activeProblems">List of active problems (some may be acknowledged)</param>
    /// <returns>The overall service status</returns>
    ServiceStatus DetermineOverallStatusWithAcknowledges(List<Metric> metrics, List<ActiveProblem>? activeProblems);
}
