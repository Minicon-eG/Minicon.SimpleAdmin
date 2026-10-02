using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Interface for collecting system and service metrics
/// </summary>
public interface IMetricsCollector : IDisposable
{
    /// <summary>
    /// Collects current metrics from the system
    /// </summary>
    Task<List<Metric>> CollectMetricsAsync(List<PrtgCheck> checks);
}
