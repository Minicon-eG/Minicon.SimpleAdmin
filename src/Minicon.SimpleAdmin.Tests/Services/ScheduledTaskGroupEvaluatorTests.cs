using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Snapshot = Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator.ServerSnapshot;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>Cross-server single-active check of scheduled tasks (ScheduledTaskGroupEvaluator).</summary>
public class ScheduledTaskGroupEvaluatorTests
{
    private const string Export = @"\Minicon\Export";

    private static Config CreateConfig(bool featureEnabled = true, params (string Name, string? Env, bool Enabled)[] servers)
    {
        var config = new Config
        {
            Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = featureEnabled } }
        };
        foreach (var (name, env, enabled) in servers)
        {
            config.Servers.Add(new Server
            {
                Name = name,
                Active = true,
                Checks = new ServerChecksConfig
                {
                    ScheduledTasks = new ServerScheduledTasksConfig
                    {
                        Enabled = enabled,
                        Tasks = { new ScheduledTaskWatchConfig { Pattern = @"\Minicon\*" } }
                    }
                }
            });
            if (env == null)
                continue;
            if (!config.Environments.TryGetValue(env, out var envConfig))
                config.Environments[env] = envConfig = new EnvironmentConfig();
            envConfig.Services.Add(name);
        }
        return config;
    }

    private static Config Prod(params string[] servers)
        => CreateConfig(true, servers.Select(s => (s, (string?)"production", true)).ToArray());

    private static Snapshot Known(string server, params (string Path, bool Enabled, ScheduledTaskMode Mode)[] tasks)
        => new(server, null, true, new ScheduledTaskState
        {
            LastChecked = DateTime.UtcNow,
            Tasks = tasks.Select(t => new ScheduledTaskInfo { Path = t.Path, Enabled = t.Enabled, State = t.Enabled ? "Ready" : "Disabled", Mode = t.Mode }).ToList()
        });

    private static Snapshot Srv(string server, bool enabled, ScheduledTaskMode mode = ScheduledTaskMode.SingleActive)
        => Known(server, (Export, enabled, mode));

    private static Snapshot Unknown(string server) => new(server, null, false, null);

    private static List<ActiveProblem> AllProblems(Dictionary<string, List<ActiveProblem>> byAnchor)
        => byAnchor.SelectMany(kv => kv.Value).ToList();

    [Theory]
    [InlineData(0, "noneactive")]
    [InlineData(2, "multiactive")]
    [InlineData(3, "multiactive")]
    public void SingleActive_WithZeroOrSeveralActiveServers_ReportsCriticalGroupProblem(int activeCount, string suffix)
    {
        var servers = new[] { "srv-a", "srv-b", "srv-c" };
        var snapshots = servers.Select((s, i) => Srv(s, i < activeCount));

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod(servers), snapshots);
        var problems = AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups));

        groups.Should().ContainSingle();
        groups[0].ActiveServers.Should().HaveCount(activeCount);
        problems.Should().ContainSingle();
        problems[0].Id.Should().EndWith(suffix);
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
    }

    [Fact]
    public void SingleActive_WithExactlyOneActiveServer_ReportsNothing()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(
            Prod("srv-a", "srv-b", "srv-c"), new[] { Srv("srv-a", false), Srv("srv-b", true), Srv("srv-c", false) });

        groups.Should().ContainSingle();
        groups[0].ActiveServers.Should().Equal("srv-b");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void UnknownServer_SuppressesNoneActive()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(
            Prod("srv-a", "srv-b", "srv-c"), new[] { Srv("srv-a", false), Srv("srv-b", false), Unknown("srv-c") });

        groups.Should().ContainSingle();
        groups[0].UnknownServers.Should().Equal("srv-c");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void ServerWithoutSnapshotOrWithFailedQuery_CountsAsUnknown()
    {
        var failed = new Snapshot("srv-b", null, true, new ScheduledTaskState { QueryFailed = true, Error = "Zugriff verweigert" });

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(
            Prod("srv-a", "srv-b", "srv-c"), new[] { Srv("srv-a", false), failed });

        groups.Should().ContainSingle();
        groups[0].UnknownServers.Should().Equal("srv-b", "srv-c");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void UnknownServer_DoesNotSuppressMultipleActive()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(
            Prod("srv-a", "srv-b", "srv-c"), new[] { Srv("srv-a", true), Srv("srv-b", true), Unknown("srv-c") });

        var problems = AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups));

        problems.Should().ContainSingle(p => p.Id.EndsWith("multiactive"));
        problems[0].Message.Should().Contain("srv-a").And.Contain("srv-b");
    }

    [Fact]
    public void SamePathInTwoEnvironments_FormsSeparateGroups()
    {
        var config = CreateConfig(true,
            ("prod-a", "production", true), ("prod-b", "production", true),
            ("ref-a", "ref", true), ("ref-b", "ref", true));

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[]
        {
            Srv("prod-a", true), Srv("prod-b", false),
            Srv("ref-a", true), Srv("ref-b", false)
        });

        groups.Should().HaveCount(2);
        groups.Select(g => g.Environment).Should().BeEquivalentTo("production", "ref");
        groups.Should().OnlyContain(g => g.ActiveServers.Count == 1);
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void ServersWithoutEnvironment_FormTheEmptyEnvironment()
    {
        var config = CreateConfig(true, ("srv-a", null, true), ("srv-b", null, true));

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true) });

        groups.Should().ContainSingle();
        groups[0].Environment.Should().BeEmpty();
        AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups)).Should().ContainSingle(p => p.Id.EndsWith("multiactive"));
    }

    [Fact]
    public void TaskPath_IsMatchedCaseInsensitively()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b"), new[]
        {
            Known("srv-a", (@"\Minicon\Export", true, ScheduledTaskMode.SingleActive)),
            Known("srv-b", (@"\MINICON\export", true, ScheduledTaskMode.SingleActive))
        });

        groups.Should().ContainSingle();
        groups[0].Members.Should().HaveCount(2);
    }

    [Fact]
    public void AllServers_HasNoGroupCheck()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b"), new[]
        {
            Srv("srv-a", true, ScheduledTaskMode.AllServers),
            Srv("srv-b", true, ScheduledTaskMode.AllServers)
        });

        groups.Should().ContainSingle(g => g.Mode == ScheduledTaskMode.AllServers);
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void MixedModes_SingleActiveWins()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b"), new[]
        {
            Srv("srv-a", true, ScheduledTaskMode.AllServers),
            Srv("srv-b", true)
        });

        groups.Should().ContainSingle(g => g.Mode == ScheduledTaskMode.SingleActive);
        AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups)).Should().ContainSingle(p => p.Id.EndsWith("multiactive"));
    }

    [Fact]
    public void Anchor_IsFirstMemberIndependentOfReadOrderAndEnabledState()
    {
        var config = Prod("srv-c", "srv-a", "srv-b");

        var multi1 = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-c", true), Srv("srv-b", true), Srv("srv-a", false) });
        var multi2 = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", false), Srv("srv-b", true), Srv("srv-c", true) });
        var none = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-c", false), Srv("srv-b", false), Srv("srv-a", false) });

        multi1[0].AnchorServer.Should().Be("srv-a");
        multi2[0].AnchorServer.Should().Be("srv-a");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(multi1).Keys.Should().Equal("srv-a");
        none[0].AnchorServer.Should().Be("srv-a");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(none).Keys.Should().Equal("srv-a");
        multi1[0].Members.Select(m => m.ServerId).Should().Equal("srv-a", "srv-b", "srv-c");
    }

    [Fact]
    public void Anchor_AndProblemId_StayWhenActiveSetShrinks()
    {
        var config = Prod("srv-a", "srv-b", "srv-c");

        var before = ScheduledTaskGroupEvaluator.DeriveGroupProblems(ScheduledTaskGroupEvaluator.BuildGroups(
            config, new[] { Srv("srv-a", true), Srv("srv-b", true), Srv("srv-c", true) }));
        var after = ScheduledTaskGroupEvaluator.DeriveGroupProblems(ScheduledTaskGroupEvaluator.BuildGroups(
            config, new[] { Srv("srv-a", false), Srv("srv-b", true), Srv("srv-c", true) }));

        before.Keys.Should().Equal("srv-a");
        after.Keys.Should().Equal("srv-a");
        after["srv-a"].Single().Id.Should().Be(before["srv-a"].Single().Id);
    }

    [Fact]
    public void Anchor_StaysWhenTheFirstMemberLeavesTheGroup()
    {
        var config = Prod("srv-a", "srv-b", "srv-c");

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[]
        {
            Known("srv-a", (@"\Other", true, ScheduledTaskMode.SingleActive)), // srv-a no longer has the task
            Srv("srv-b", true), Srv("srv-c", true)
        });

        // Anchor = first participant of the environment, even if it does not have the task.
        groups.Single(g => g.TaskPath == Export).AnchorServer.Should().Be("srv-a");
    }

    [Fact]
    public void Anchor_StaysWhenItBecomesUnknown()
    {
        var config = Prod("srv-a", "srv-b", "srv-c");

        var known = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true), Srv("srv-c", true) });
        var timedOut = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Unknown("srv-a"), Srv("srv-b", true), Srv("srv-c", true) });

        known[0].AnchorServer.Should().Be("srv-a");
        timedOut[0].AnchorServer.Should().Be("srv-a");
        timedOut[0].UnknownServers.Should().Equal("srv-a");
        var problem = ScheduledTaskGroupEvaluator.DeriveGroupProblems(timedOut)["srv-a"].Single();
        problem.Id.Should().Be(ScheduledTaskGroupEvaluator.DeriveGroupProblems(known)["srv-a"].Single().Id);
        problem.Message.Should().Contain("srv-b").And.Contain("srv-c");
    }

    [Fact]
    public void Anchor_IsPerEnvironment_AndIgnoresNonParticipants()
    {
        var config = CreateConfig(true,
            ("a-off", "production", false), ("p-b", "production", true), ("p-c", "production", true),
            ("r-x", "ref", true), ("r-y", "ref", true));

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[]
        {
            Srv("p-b", true), Srv("p-c", true), Srv("r-x", true), Srv("r-y", true)
        });

        groups.Single(g => g.Environment == "production").AnchorServer.Should().Be("p-b");
        groups.Single(g => g.Environment == "ref").AnchorServer.Should().Be("r-x");
    }

    [Fact]
    public void ServerWithoutWatchPatterns_DoesNotTakePart_AndIsNotUnknown()
    {
        var config = Prod("srv-a", "srv-b", "srv-c");
        config.Servers.Single(s => s.Name == "srv-c").Checks!.ScheduledTasks!.Tasks.Clear();

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", false), Srv("srv-b", false) });

        groups[0].UnknownServers.Should().BeEmpty();
        AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups)).Should().ContainSingle(p => p.Id.EndsWith("noneactive"));
    }

    [Fact]
    public void InactiveServer_DoesNotTakePart()
    {
        var config = Prod("srv-a", "srv-b");
        config.Servers.Single(s => s.Name == "srv-b").Active = false;

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true) });

        groups[0].Members.Select(m => m.ServerId).Should().Equal("srv-a");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void ServerInTwoEnvironments_TakesPartInBoth()
    {
        var config = CreateConfig(true, ("prod-a", "production", true), ("shared", "production", true), ("ref-a", "ref", true));
        config.Environments["ref"].Services.Add("shared");

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("prod-a", true), Srv("shared", true), Srv("ref-a", false) });

        groups.Should().HaveCount(2);
        groups.Single(g => g.Environment == "production").ActiveServers.Should().Equal("prod-a", "shared");
        groups.Single(g => g.Environment == "ref").ActiveServers.Should().Equal("shared");
        AllProblems(ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups)).Should().ContainSingle(p => p.Id.EndsWith("multiactive"));
    }

    [Fact]
    public void UnknownServerInTwoEnvironments_SuppressesNoneActiveInBoth()
    {
        var config = CreateConfig(true, ("prod-a", "production", true), ("shared", "production", true), ("ref-a", "ref", true));
        config.Environments["ref"].Services.Add("shared");

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("prod-a", false), Unknown("shared"), Srv("ref-a", false) });

        groups.Should().OnlyContain(g => g.UnknownServers.SequenceEqual(new[] { "shared" }));
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void ServerIdDifferentFromName_IsMatchedByIdOrName()
    {
        var config = Prod("host-a", "host-b");
        config.Servers.Single(s => s.Name == "host-a").Id = "srv-a";

        var byId = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("host-b", true) });
        var byName = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("host-a", true), Srv("host-b", true) });

        byId[0].Members.Select(m => m.ServerId).Should().Equal("host-b", "srv-a");
        byId[0].UnknownServers.Should().BeEmpty();
        byId[0].AnchorServer.Should().Be("host-a"); // anchor = config name
        byName[0].Members.Select(m => m.ServerId).Should().Equal("host-a", "host-b");
    }

    [Fact]
    public void UnreachablePlaceholder_CountsAsUnknown()
    {
        var now = DateTime.UtcNow;
        var statuses = new[]
        {
            new RuntimeStatus { Server = "srv-a", Service = "api", LastUpdate = now, ScheduledTasks = new ScheduledTaskState { Tasks = { new ScheduledTaskInfo { Path = Export } } } },
            new RuntimeStatus { Server = "srv-b", Service = "erreichbarkeit", LastUpdate = now, Status = ServiceStatus.Unknown }
        };

        var snapshots = ScheduledTaskGroupEvaluator.SnapshotsFromStatuses(statuses, now, TimeSpan.FromMinutes(10));
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b"), snapshots);

        snapshots.Single(s => s.ServerId == "srv-b").Known.Should().BeFalse();
        groups[0].UnknownServers.Should().Equal("srv-b");
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)] // 0 known active + unknown → maybe none, maybe several
    [InlineData(true, false)]  // 1 known active + unknown → maybe several
    public void PendingIds_BothIdsWhileUndecidable(bool aActive, bool bActive)
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b", "srv-c"),
            new[] { Srv("srv-a", aActive), Srv("srv-b", bActive), Unknown("srv-c") });

        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
        var pending = ScheduledTaskGroupEvaluator.DerivePendingGroupProblemIds(groups);
        pending.Keys.Should().Equal("srv-a");
        pending["srv-a"].Should().HaveCount(2)
            .And.Contain(id => id.EndsWith("noneactive"))
            .And.Contain(id => id.EndsWith("multiactive"));
    }

    [Fact]
    public void PendingIds_TwoKnownActive_MultipleActiveIsReported_NoneActiveStaysPending()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b", "srv-c"),
            new[] { Srv("srv-a", true), Srv("srv-b", true), Unknown("srv-c") });

        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups)["srv-a"].Should().ContainSingle(p => p.Id.EndsWith("multiactive"));
        ScheduledTaskGroupEvaluator.DerivePendingGroupProblemIds(groups)["srv-a"].Should().ContainSingle(id => id.EndsWith("noneactive"));
    }

    [Fact]
    public void PendingIds_EmptyWhenAllKnown()
    {
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(Prod("srv-a", "srv-b", "srv-c"),
            new[] { Srv("srv-a", false), Srv("srv-b", true), Srv("srv-c", false) });

        ScheduledTaskGroupEvaluator.DerivePendingGroupProblemIds(groups).Should().BeEmpty();
    }

    [Fact]
    public void UnknownParticipants_AreListedEvenWithoutAnyGroup()
    {
        var config = CreateConfig(true, ("srv-a", "production", true), ("srv-b", "production", true), ("x", null, true));

        var unknown = ScheduledTaskGroupEvaluator.UnknownParticipantsByEnvironment(config, new[] { Unknown("srv-a"), Unknown("srv-b") });

        ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Unknown("srv-a"), Unknown("srv-b") }).Should().BeEmpty();
        unknown["production"].Should().Equal("srv-a", "srv-b");
        unknown[""].Should().Equal("x");
    }

    [Fact]
    public void IsHeldByUnknownParticipants_MatchesGroupIdsOfEnvironmentsWithUnknownServers()
    {
        var unknown = new Dictionary<string, IReadOnlyList<string>> { ["production"] = new[] { "srv-c" } };
        var prodNone = ActiveProblem.ScheduledTaskNoneActive("production", Export, new[] { "a" }).Id;
        var prodMulti = ActiveProblem.ScheduledTaskMultipleActive("production", Export, new[] { "a", "b" }).Id;
        var refNone = ActiveProblem.ScheduledTaskNoneActive("ref", Export, new[] { "a" }).Id;
        var perServer = ActiveProblem.ScheduledTaskMissedRun(Export, null, null).Id;

        ScheduledTaskGroupEvaluator.IsHeldByUnknownParticipants(prodNone, unknown).Should().BeTrue();
        ScheduledTaskGroupEvaluator.IsHeldByUnknownParticipants(prodMulti, unknown).Should().BeTrue();
        ScheduledTaskGroupEvaluator.IsHeldByUnknownParticipants(refNone, unknown).Should().BeFalse();
        ScheduledTaskGroupEvaluator.IsHeldByUnknownParticipants(perServer, unknown).Should().BeFalse();

        var noEnv = new Dictionary<string, IReadOnlyList<string>> { [""] = new[] { "x" } };
        ScheduledTaskGroupEvaluator.IsHeldByUnknownParticipants(
            ActiveProblem.ScheduledTaskNoneActive("", Export, new[] { "x" }).Id, noEnv).Should().BeTrue();
    }

    [Fact]
    public void GetStaleAfter_DefaultsToNotifierThreshold()
    {
        ScheduledTaskGroupEvaluator.GetStaleAfter(new Config()).Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void GetStaleAfter_GrowsWithTheWorkerInterval()
    {
        var config = new Config { General = new GeneralSettings { CheckIntervalSeconds = 300 } };
        ScheduledTaskGroupEvaluator.GetStaleAfter(config).Should().Be(TimeSpan.FromSeconds(3 * 300 + 240));

        config.Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { CheckIntervalSeconds = 600 } };
        ScheduledTaskGroupEvaluator.GetStaleAfter(config).Should().Be(TimeSpan.FromSeconds(3 * 600 + 240));
    }

    [Fact]
    public void GetStaleAfter_UsesLargerNotifierThreshold()
    {
        var config = new Config { Features = new FeaturesConfig { Notifications = new NotificationsFeatureConfig { StaleThresholdMinutes = 45 } } };

        ScheduledTaskGroupEvaluator.GetStaleAfter(config).Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void FeatureDisabledGlobally_YieldsNoGroups()
    {
        var config = CreateConfig(false, ("srv-a", "production", true), ("srv-b", "production", true));

        ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true) })
            .Should().BeEmpty();
    }

    [Fact]
    public void FeatureDisabledPerServer_ServerDoesNotTakePart()
    {
        var config = CreateConfig(true, ("srv-a", "production", true), ("srv-b", "production", false));

        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true) });

        groups.Should().ContainSingle();
        groups[0].Members.Select(m => m.ServerId).Should().Equal("srv-a");
        groups[0].UnknownServers.Should().BeEmpty();
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups).Should().BeEmpty();
    }

    [Fact]
    public void FeatureDisabledOnAllServers_YieldsNoGroups()
    {
        var config = CreateConfig(true, ("srv-a", "production", false), ("srv-b", "production", false));

        ScheduledTaskGroupEvaluator.BuildGroups(config, new[] { Srv("srv-a", true), Srv("srv-b", true) })
            .Should().BeEmpty();
    }

    [Fact]
    public void GroupProblemIds_AreStableAndDifferPerEnvironment()
    {
        var p1 = ActiveProblem.ScheduledTaskMultipleActive("production", Export, new[] { "a", "b" });
        var p2 = ActiveProblem.ScheduledTaskMultipleActive("production", Export, new[] { "a", "b", "c" });
        var p3 = ActiveProblem.ScheduledTaskMultipleActive("ref", Export, new[] { "a", "b" });

        p1.Id.Should().Be(p2.Id);
        p1.Id.Should().NotBe(p3.Id);
    }

    [Fact]
    public void SnapshotsFromStatuses_NewestQueryWins_AndStaleStatusIsUnknown()
    {
        var now = DateTime.UtcNow;
        RuntimeStatus Status(string server, string service, DateTime lastUpdate, DateTime? checkedAt, bool enabled) => new()
        {
            Server = server,
            Service = service,
            LastUpdate = lastUpdate,
            ScheduledTasks = checkedAt == null ? null : new ScheduledTaskState
            {
                LastChecked = checkedAt.Value,
                Tasks = { new ScheduledTaskInfo { Path = Export, Enabled = enabled } }
            }
        };

        var snapshots = ScheduledTaskGroupEvaluator.SnapshotsFromStatuses(new[]
        {
            Status("srv-a", "api", now, now.AddMinutes(-5), enabled: false),
            Status("srv-a", "web", now, now, enabled: true),
            Status("srv-b", "api", now.AddHours(-2), now.AddHours(-2), enabled: true),
            Status("srv-c", "api", now, null, enabled: false)
        }, now, TimeSpan.FromMinutes(10));

        snapshots.Single(s => s.ServerId == "srv-a").Should().Match<Snapshot>(s => s.Known && s.State!.Tasks[0].Enabled);
        snapshots.Single(s => s.ServerId == "srv-b").Known.Should().BeFalse();
        snapshots.Single(s => s.ServerId == "srv-c").Known.Should().BeFalse();
    }
}
