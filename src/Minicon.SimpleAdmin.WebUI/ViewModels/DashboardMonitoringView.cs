using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.WebUI.ViewModels;

/// <summary>
/// Monitoring summary for the dashboard: open/critical/acknowledged problem counts,
/// servers that are not healthy and the most urgent open problems.
/// </summary>
public sealed class DashboardMonitoringView
{
    /// <summary>Number of urgent problems listed on the dashboard.</summary>
    internal const int TopProblemCount = 5;

    public int ServerCount { get; init; }
    public int OpenProblems { get; init; }
    public int OpenCritical { get; init; }
    public int Acknowledged { get; init; }
    public int UnhealthyServers { get; init; }
    public List<(string ServerId, ActiveProblem Problem)> TopProblems { get; init; } = new();

    /// <summary>Set when the status data could not be read; the dashboard then shows a hint instead of numbers.</summary>
    public string? Error { get; init; }

    public static DashboardMonitoringView Unavailable(string error) => new() { Error = error };

    public static DashboardMonitoringView Build(IReadOnlyCollection<ServerStatusView> servers)
    {
        var problems = servers
            .SelectMany(s => s.ActiveProblems.Select(p => (s.ServerId, Problem: p)))
            .ToList();
        var open = problems.Where(p => !p.Problem.Acknowledged).ToList();

        return new DashboardMonitoringView
        {
            ServerCount = servers.Count,
            OpenProblems = open.Count,
            OpenCritical = open.Count(p => p.Problem.Severity == ProblemSeverity.Critical),
            Acknowledged = problems.Count - open.Count,
            UnhealthyServers = servers.Count(s => s.OverallStatus is ServiceStatus.Unhealthy
                or ServiceStatus.Degraded or ServiceStatus.Unreachable),
            TopProblems = open
                .OrderByDescending(p => p.Problem.Severity)
                .ThenBy(p => p.Problem.FirstOccurrence)
                .Take(TopProblemCount)
                .ToList()
        };
    }
}
