using FluentAssertions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Moq;
using Metric = Minicon.SimpleAdmin.Models.Status.Metric;

namespace Minicon.SimpleAdmin.Tests.Services;

public class BizTalkCheckerTests
{
    // --- pure evaluators ---

    [Fact]
    public void EvaluateArtifacts_StoppedArtifact_YieldsCriticalProblem()
    {
        var items = new[] { ("App1", "Started", false), ("App2", "Stopped", false) };
        var problems = BizTalkChecker.EvaluateArtifacts("application", items, new BizTalkArtifactMonitor { Monitor = "all" }).ToList();

        problems.Should().ContainSingle();
        problems[0].Name.Should().Be("App2");
        problems[0].ArtifactType.Should().Be("application");
        problems[0].IsCritical.Should().BeTrue();
    }

    [Fact]
    public void EvaluateArtifacts_SystemArtifact_SkippedInAllMode()
    {
        var items = new[] { ("BizTalk.System", "Stopped", true) };
        BizTalkChecker.EvaluateArtifacts("application", items, new BizTalkArtifactMonitor { Monitor = "all" })
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateArtifacts_ExcludeList_SkipsExcluded()
    {
        var items = new[] { ("AppX", "Stopped", false) };
        BizTalkChecker.EvaluateArtifacts("application", items, new BizTalkArtifactMonitor { Monitor = "all", Exclude = new() { "appx" } })
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateArtifacts_ListMode_OnlyIncludedAreChecked()
    {
        var items = new[] { ("AppA", "Stopped", false), ("AppB", "Stopped", false) };
        var problems = BizTalkChecker.EvaluateArtifacts("sendport", items, new BizTalkArtifactMonitor { Monitor = "list", Include = new() { "AppA" } }).ToList();

        problems.Should().ContainSingle();
        problems[0].Name.Should().Be("AppA");
    }

    [Fact]
    public void EvaluateArtifacts_NoneMode_Empty()
    {
        var items = new[] { ("AppA", "Stopped", false) };
        BizTalkChecker.EvaluateArtifacts("application", items, new BizTalkArtifactMonitor { Monitor = "none" })
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateReceiveLocations_Disabled_UsesConfiguredSeverity()
    {
        var rls = new List<BtReceiveLocation>
        {
            new() { Name = "RL_A", Enable = true },
            new() { Name = "RL_B", Enable = false },
            new() { Name = "RL_C", Enable = false }
        };
        var monitor = new BizTalkReceiveLocationMonitor { Monitor = "all", DisabledSeverity = "Warning", CriticalList = new() { "RL_C" } };

        var problems = BizTalkChecker.EvaluateReceiveLocations(rls, monitor).ToList();

        problems.Should().HaveCount(2);
        problems.Single(p => p.Name == "RL_B").IsCritical.Should().BeFalse();
        problems.Single(p => p.Name == "RL_C").IsCritical.Should().BeTrue();
    }

    [Fact]
    public void EvaluateSuspended_AppliesThresholdsAndGrouping()
    {
        var instances = new List<BtInstance>
        {
            new() { InstanceStatus = "Suspended (resumable)", Application = "App1" },
            new() { InstanceStatus = "Suspended (not resumable)", Application = "App1" },
            new() { InstanceStatus = "Active", Application = "App2" }
        };
        var state = new BizTalkState();

        BizTalkChecker.EvaluateSuspended(
            instances,
            new BizTalkSuspendedConfig { Enabled = true, Warning = 1, Critical = 5, ByApplication = true },
            new BizTalkDefaults(),
            state);

        state.SuspendedTotal.Should().Be(2);
        state.SuspendedStatus.Should().Be(MetricStatus.Warning);
        state.SuspendedGroups.Should().ContainSingle(g => g.Scope == "App1" && g.Count == 2);
    }

    // --- orchestration via mocked API client ---

    private static Mock<IBizTalkApiClient> HealthyApi()
    {
        var api = new Mock<IBizTalkApiClient>();
        api.Setup(a => a.GetApplicationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BtApplication> { new() { Name = "App1", Status = "Started" } });
        api.Setup(a => a.GetOrchestrationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BtOrchestration> { new() { FullName = "O1", Status = "Started" } });
        api.Setup(a => a.GetSendPortsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BtSendPort> { new() { Name = "SP1", Status = "Started" } });
        api.Setup(a => a.GetReceiveLocationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BtReceiveLocation> { new() { Name = "RL1", Enable = true } });
        api.Setup(a => a.GetInstancesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BtInstance>());
        return api;
    }

    private static BizTalkChecker Build(IBizTalkApiClient api) => new(api, new Mock<ILogger<BizTalkChecker>>().Object);

    [Fact]
    public async Task CheckAsync_AllHealthy_NoProblems()
    {
        var state = await Build(HealthyApi().Object).CheckAsync(new ServerBizTalkConfig { Enabled = true }, new BizTalkDefaults());

        state.ApiReachable.Should().BeTrue();
        state.Problems.Should().BeEmpty();
        state.SuspendedStatus.Should().Be(MetricStatus.Healthy);
        state.ApplicationsTotal.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_ApiThrows_ReturnsUnreachable()
    {
        var api = HealthyApi();
        api.Setup(a => a.GetApplicationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("management api down"));

        var state = await Build(api.Object).CheckAsync(new ServerBizTalkConfig { Enabled = true }, new BizTalkDefaults());

        state.ApiReachable.Should().BeFalse();
        state.ApiError.Should().Contain("down");
    }

    // --- ProblemDerivationService BizTalk branch ---

    [Fact]
    public void DeriveProblems_BizTalkApiUnreachable_YieldsApiError()
    {
        var bt = new BizTalkState { ApiReachable = false, ApiError = "x" };
        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, null, null, null, null, null, bt);

        problems.Should().ContainSingle(p => p.Id == "biztalk_api_error" && p.Severity == ProblemSeverity.Critical);
    }

    [Fact]
    public void DeriveProblems_BizTalkProblemsAndSuspended_MapToActiveProblems()
    {
        var bt = new BizTalkState
        {
            ApiReachable = true,
            Problems =
            {
                new BizTalkArtifact { ArtifactType = "application", Name = "AppX", Status = "Stopped", IsCritical = true },
                new BizTalkArtifact { ArtifactType = "receivelocation", Name = "RLX", Status = "Disabled", IsCritical = false }
            },
            SuspendedTotal = 7,
            SuspendedStatus = MetricStatus.Critical
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, null, null, null, null, null, bt);

        problems.Should().Contain(p => p.Id == "biztalk_app_appx_stopped" && p.Severity == ProblemSeverity.Critical);
        problems.Should().Contain(p => p.Id == "biztalk_rl_rlx_disabled" && p.Severity == ProblemSeverity.Warning);
        problems.Should().Contain(p => p.Id == "biztalk_suspended_instances" && p.Severity == ProblemSeverity.Critical);
    }
}
