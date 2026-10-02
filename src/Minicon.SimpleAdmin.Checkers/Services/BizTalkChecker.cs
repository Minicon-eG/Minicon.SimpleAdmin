using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Polls the BizTalk Management API and derives a compact <see cref="BizTalkState"/> (problematic
/// artefacts + totals + suspended-instance summary). The IO is behind <see cref="IBizTalkApiClient"/>;
/// the evaluation is pure and unit-testable. Any API failure yields ApiReachable = false.
/// </summary>
public interface IBizTalkChecker
{
    Task<BizTalkState> CheckAsync(ServerBizTalkConfig config, BizTalkDefaults defaults, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BizTalkChecker(IBizTalkApiClient apiClient, ILogger<BizTalkChecker> logger) : IBizTalkChecker
{
    public async Task<BizTalkState> CheckAsync(ServerBizTalkConfig config, BizTalkDefaults defaults, CancellationToken cancellationToken = default)
    {
        var baseUrl = config.BaseUrl;
        try
        {
            var appsTask = apiClient.GetApplicationsAsync(baseUrl, cancellationToken);
            var orchTask = apiClient.GetOrchestrationsAsync(baseUrl, cancellationToken);
            var sendPortsTask = apiClient.GetSendPortsAsync(baseUrl, cancellationToken);
            var rlTask = apiClient.GetReceiveLocationsAsync(baseUrl, cancellationToken);
            var instancesTask = config.SuspendedInstances.Enabled
                ? apiClient.GetInstancesAsync(baseUrl, cancellationToken)
                : Task.FromResult(new List<BtInstance>());

            await Task.WhenAll(appsTask, orchTask, sendPortsTask, rlTask, instancesTask);

            var apps = appsTask.Result;
            var orchestrations = orchTask.Result;
            var sendPorts = sendPortsTask.Result;
            var receiveLocations = rlTask.Result;
            var instances = instancesTask.Result;

            var state = new BizTalkState
            {
                ApiReachable = true,
                ApplicationsTotal = apps.Count,
                OrchestrationsTotal = orchestrations.Count,
                SendPortsTotal = sendPorts.Count,
                ReceiveLocationsTotal = receiveLocations.Count
            };

            state.Problems.AddRange(EvaluateArtifacts("application",
                apps.Select(a => (a.Name ?? string.Empty, a.Status ?? string.Empty, a.IsSystem)), config.Applications));
            state.Problems.AddRange(EvaluateArtifacts("orchestration",
                orchestrations.Select(o => (o.FullName ?? string.Empty, o.Status ?? string.Empty, false)), config.Orchestrations));
            state.Problems.AddRange(EvaluateArtifacts("sendport",
                sendPorts.Select(s => (s.Name ?? string.Empty, s.Status ?? string.Empty, false)), config.SendPorts));
            state.Problems.AddRange(EvaluateReceiveLocations(receiveLocations, config.ReceiveLocations));

            if (config.SuspendedInstances.Enabled)
                EvaluateSuspended(instances, config.SuspendedInstances, defaults, state);

            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BizTalk: Management-API '{BaseUrl}' nicht erreichbar", baseUrl);
            return new BizTalkState { ApiReachable = false, ApiError = ex.Message };
        }
    }

    // --- pure evaluation (testable) ---

    internal static IEnumerable<BizTalkArtifact> EvaluateArtifacts(
        string artifactType,
        IEnumerable<(string Name, string Status, bool IsSystem)> items,
        BizTalkArtifactMonitor monitor)
    {
        if (string.Equals(monitor.Monitor, "none", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var (name, status, isSystem) in items)
        {
            if (string.IsNullOrEmpty(name) || !IsMonitored(name, isSystem, monitor))
                continue;
            if (!IsStarted(status))
                yield return new BizTalkArtifact { ArtifactType = artifactType, Name = name, Status = status, IsCritical = true };
        }
    }

    internal static IEnumerable<BizTalkArtifact> EvaluateReceiveLocations(
        IEnumerable<BtReceiveLocation> receiveLocations,
        BizTalkReceiveLocationMonitor monitor)
    {
        if (string.Equals(monitor.Monitor, "none", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var rl in receiveLocations)
        {
            var name = rl.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name) || !IsMonitored(name, false, monitor))
                continue;
            if (!rl.Enable)
            {
                var critical = string.Equals(monitor.DisabledSeverity, "Critical", StringComparison.OrdinalIgnoreCase)
                               || monitor.CriticalList.Contains(name, StringComparer.OrdinalIgnoreCase);
                yield return new BizTalkArtifact { ArtifactType = "receivelocation", Name = name, Status = "Disabled", IsCritical = critical };
            }
        }
    }

    internal static void EvaluateSuspended(
        IReadOnlyList<BtInstance> instances,
        BizTalkSuspendedConfig cfg,
        BizTalkDefaults defaults,
        BizTalkState state)
    {
        var suspended = instances
            .Where(i => (i.InstanceStatus ?? string.Empty).Contains("Suspended", StringComparison.OrdinalIgnoreCase))
            .ToList();

        state.SuspendedTotal = suspended.Count;

        var warning = cfg.Warning ?? defaults.SuspendedWarning;
        var critical = cfg.Critical ?? defaults.SuspendedCritical;
        state.SuspendedStatus = suspended.Count >= critical ? MetricStatus.Critical
            : suspended.Count >= warning ? MetricStatus.Warning
            : MetricStatus.Healthy;

        if (cfg.ByApplication)
        {
            state.SuspendedGroups = suspended
                .GroupBy(i => string.IsNullOrEmpty(i.Application) ? "(unbekannt)" : i.Application!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new BizTalkSuspendedGroup { Scope = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();
        }
    }

    private static bool IsMonitored(string name, bool isSystem, BizTalkArtifactMonitor monitor)
    {
        if (string.Equals(monitor.Monitor, "list", StringComparison.OrdinalIgnoreCase))
            return monitor.Include.Contains(name, StringComparer.OrdinalIgnoreCase);

        // "all": skip excluded names and (unless explicitly included) BizTalk system artefacts.
        if (monitor.Exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;
        if (isSystem && !monitor.Include.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsStarted(string status) => string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase);
}
