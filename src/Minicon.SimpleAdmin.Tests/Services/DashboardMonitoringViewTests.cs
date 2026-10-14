using FluentAssertions;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.Tests.Services;

public class DashboardMonitoringViewTests
{
    private static ServerStatusView Server(string id, ServiceStatus status, params ActiveProblem[] problems) => new()
    {
        ServerId = id,
        OverallStatus = status,
        ActiveProblems = problems.ToList()
    };

    private static ActiveProblem Problem(string id, ProblemSeverity severity, bool acknowledged = false, int minutesAgo = 10) => new()
    {
        Id = id,
        Type = "test",
        Severity = severity,
        Message = id,
        Acknowledged = acknowledged,
        Since = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    [Fact]
    public void Build_NoServers_ReturnsZeroCounts()
    {
        var view = DashboardMonitoringView.Build([]);

        view.ServerCount.Should().Be(0);
        view.OpenProblems.Should().Be(0);
        view.TopProblems.Should().BeEmpty();
        view.Error.Should().BeNull();
    }

    [Fact]
    public void Build_CountsOpenCriticalAcknowledgedAndUnhealthyServers()
    {
        var servers = new List<ServerStatusView>
        {
            Server("srv1", ServiceStatus.Unhealthy,
                Problem("cpu", ProblemSeverity.Critical),
                Problem("disk", ProblemSeverity.Warning)),
            Server("srv2", ServiceStatus.Acknowledged, Problem("pool", ProblemSeverity.Critical, acknowledged: true)),
            Server("srv3", ServiceStatus.Unreachable),
            Server("srv4", ServiceStatus.Degraded),
            Server("srv5", ServiceStatus.Healthy)
        };

        var view = DashboardMonitoringView.Build(servers);

        view.ServerCount.Should().Be(5);
        view.OpenProblems.Should().Be(2);
        view.OpenCritical.Should().Be(1);
        view.Acknowledged.Should().Be(1);
        view.UnhealthyServers.Should().Be(3, "Unhealthy, Unreachable und Degraded zählen als nicht gesund");
    }

    [Fact]
    public void Build_TopProblems_OnlyOpen_CriticalFirst_OldestFirst_Limited()
    {
        var problems = Enumerable.Range(1, 6)
            .Select(i => Problem($"warn{i}", ProblemSeverity.Warning, minutesAgo: i))
            .Append(Problem("crit-new", ProblemSeverity.Critical, minutesAgo: 1))
            .Append(Problem("crit-old", ProblemSeverity.Critical, minutesAgo: 60))
            .Append(Problem("acked", ProblemSeverity.Critical, acknowledged: true))
            .ToArray();

        var view = DashboardMonitoringView.Build([Server("srv1", ServiceStatus.Unhealthy, problems)]);

        view.TopProblems.Should().HaveCount(DashboardMonitoringView.TopProblemCount);
        view.TopProblems.Select(p => p.Problem.Id).Should().StartWith(new[] { "crit-old", "crit-new", "warn6" });
        view.TopProblems.Should().NotContain(p => p.Problem.Acknowledged);
        view.TopProblems.Should().OnlyContain(p => p.ServerId == "srv1");
    }

    [Fact]
    public void Unavailable_CarriesError()
    {
        DashboardMonitoringView.Unavailable("kaputt").Error.Should().Be("kaputt");
    }
}
