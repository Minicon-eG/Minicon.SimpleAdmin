using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services;
using Moq;
using MetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>Per-server problems of scheduled tasks and their central evaluation in the StatusReaderService.</summary>
public class ScheduledTaskProblemDerivationTests : IDisposable
{
    private const string Export = @"\Minicon\Export";
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static List<ActiveProblem> Derive(ScheduledTaskState state)
        => ProblemDerivationService.DeriveProblems(new List<Metric>(), null, scheduledTasks: state);

    // ── Mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public void HealthyTasks_YieldNoProblems()
    {
        Derive(new ScheduledTaskState { Tasks = { new ScheduledTaskInfo { Path = Export, Enabled = true } } })
            .Should().BeEmpty();
    }

    [Fact]
    public void LastRunFailed_MapsToFailedProblem_WithSeverityWikiAndSteps()
    {
        var lastRun = DateTime.UtcNow.AddMinutes(-10);
        var problems = Derive(new ScheduledTaskState
        {
            Tasks =
            {
                new ScheduledTaskInfo
                {
                    Path = Export, LastRunFailed = true, FailureIsCritical = false, LastTaskResult = unchecked((int)0x80070005),
                    LastRunTime = lastRun, WikiUrl = "https://wiki/export", RemediationSteps = { "Konto prüfen" }
                }
            }
        });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().EndWith("_failed");
        p.Severity.Should().Be(ProblemSeverity.Warning);
        p.Message.Should().Contain("0x80070005");
        p.WikiUrl.Should().Be("https://wiki/export");
        p.RemediationSteps.Should().Equal("Konto prüfen");
    }

    [Fact]
    public void RuntimeExceeded_UsesRunningSinceRelativeToLastChecked()
    {
        var checkedAt = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var problems = Derive(new ScheduledTaskState
        {
            LastChecked = checkedAt,
            Tasks = { new ScheduledTaskInfo { Path = Export, State = "Running", RuntimeExceeded = true, MaxRuntimeMinutes = 60, RunningSince = checkedAt.AddMinutes(-90) } }
        });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().EndWith("_runtime");
        p.Severity.Should().Be(ProblemSeverity.Warning);
        p.Message.Should().Contain("90 min").And.Contain("60 min");
    }

    [Fact]
    public void MissedRun_MapsToMissedProblem_WithExpectedRunTime()
    {
        var next = new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc);
        var problems = Derive(new ScheduledTaskState
        {
            Tasks = { new ScheduledTaskInfo { Path = Export, Enabled = true, MissedRun = true, NextRunTime = next } }
        });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().EndWith("_missed");
        p.Message.Should().NotContain("2026-11-05").And.NotContain("UTC"); // raw time only in the context
        p.Context["expectedRunTime"].ToString().Should().StartWith("2026-11-05T06:00");
    }

    [Fact]
    public void MissingResultCodeAndRunningSince_AreReportedAsUnknown()
    {
        var problems = Derive(new ScheduledTaskState
        {
            LastChecked = DateTime.UtcNow,
            Tasks = { new ScheduledTaskInfo { Path = Export, LastRunFailed = true, LastTaskResult = null, RuntimeExceeded = true, MaxRuntimeMinutes = 60, RunningSince = null } }
        });

        problems.Single(p => p.Id.EndsWith("_failed")).Message.Should().Contain("Ergebnis unbekannt");
        problems.Single(p => p.Id.EndsWith("_runtime")).Message.Should().Contain("läuft länger als erlaubt (60 min)");
    }

    [Fact]
    public void SeveralFlagsOnOneTask_YieldOneProblemEach()
    {
        var problems = Derive(new ScheduledTaskState
        {
            Tasks = { new ScheduledTaskInfo { Path = Export, LastRunFailed = true, LastTaskResult = 1, RuntimeExceeded = true, MaxRuntimeMinutes = 5, MissedRun = true } }
        });

        problems.Select(p => p.Id.Split('_').Last()).Should().BeEquivalentTo("failed", "runtime", "missed");
        problems.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(MetricStatus.Healthy, null)]
    [InlineData(MetricStatus.Warning, ProblemSeverity.Warning)]
    [InlineData(MetricStatus.Critical, ProblemSeverity.Critical)]
    public void EventStatus_MapsToEventProblem(MetricStatus status, ProblemSeverity? expected)
    {
        var problems = Derive(new ScheduledTaskState
        {
            EventStatus = status,
            EventCount = 3,
            RecentEvents = { new ScheduledTaskEvent { EventId = 103, TaskPath = Export, Message = "Aktion fehlgeschlagen" } }
        });

        if (expected == null)
        {
            problems.Should().BeEmpty();
            return;
        }

        var p = problems.Should().ContainSingle().Subject;
        p.Severity.Should().Be(expected.Value);
        p.Message.Should().Contain("3 Fehler-Ereignisse").And.Contain(Export);
    }

    [Fact]
    public void QueryFailed_YieldsOnlyTheQueryError()
    {
        var problems = Derive(new ScheduledTaskState
        {
            QueryFailed = true,
            Error = "Zugriff verweigert",
            EventStatus = MetricStatus.Critical,
            Tasks = { new ScheduledTaskInfo { Path = Export, LastRunFailed = true } }
        });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().Be(ActiveProblem.ScheduledTaskQueryError("x").Id);
        p.Message.Should().Contain("Zugriff verweigert");
    }

    [Fact]
    public void PatternWithoutHits_MapsToPatternNoMatch()
    {
        var problems = Derive(new ScheduledTaskState { PatternMatches = { [@"\Minicon\*"] = 2, [@"\Expor*"] = 0 } });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().Be(ActiveProblem.ScheduledTaskPatternNoMatch(@"\Expor*").Id);
    }

    [Fact]
    public void InfoError_MapsToInfoUnavailable()
    {
        var problems = Derive(new ScheduledTaskState
        {
            Tasks = { new ScheduledTaskInfo { Path = Export, InfoError = "Zugriff verweigert", WikiUrl = "https://wiki/export", RemediationSteps = { "Rechte prüfen" } } }
        });

        var p = problems.Should().ContainSingle(p => p.Id == ActiveProblem.ScheduledTaskInfoUnavailable(Export, "x").Id).Subject;
        p.WikiUrl.Should().Be("https://wiki/export");
        p.RemediationSteps.Should().Equal("Rechte prüfen");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "Kanal nicht gefunden")]
    public void EventsNotReadable_MapToEventsUnavailable(bool disabled, string? error)
    {
        var problems = Derive(new ScheduledTaskState { EventLogDisabled = disabled, EventError = error });

        var p = problems.Should().ContainSingle().Subject;
        p.Id.Should().Be(ActiveProblem.ScheduledTaskEventsUnavailable("x").Id);
        p.Message.Should().Contain(disabled ? "Aufgabenverlauf ist deaktiviert" : "Aufgabenverlauf konnte nicht gelesen werden – Kanal nicht gefunden");
    }

    [Fact]
    public void AcknowledgeOverload_PassesScheduledTasksAndAppliesAcknowledges()
    {
        var state = new ScheduledTaskState { Tasks = { new ScheduledTaskInfo { Path = Export, MissedRun = true } } };
        var id = Derive(state).Single().Id;
        var ack = Acknowledge.Create("srv-a", id, "tester", "", 60);

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, new[] { ack }, "srv-a", scheduledTasks: state);

        problems.Should().ContainSingle(p => p.Id == id && p.Acknowledged);
    }

    // ── StatusReaderService ─────────────────────────────────────────────────

    private ConfigurationService CreateConfig(object? ignoreRules = null)
    {
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        object Srv(string name) => new
        {
            name,
            active = true,
            serviceTypes = new[] { new { type = "api" } },
            checks = new { scheduledTasks = new { enabled = true, tasks = new[] { new { pattern = @"\Minicon\*" } } } }
        };
        configurationService.LoadFromJson(JsonSerializer.Serialize(new
        {
            output = new { pullStatusOverHttp = false },
            environments = new { production = new { services = new[] { "srv-a", "srv-b", "srv-c" } } },
            features = new
            {
                scheduledTasks = new
                {
                    enabled = true,
                    defaults = new { eventWarningCount = 1, eventCriticalCount = 5 },
                    ignoreRules = ignoreRules ?? Array.Empty<object>()
                }
            },
            servers = new[] { Srv("srv-a"), Srv("srv-b"), Srv("srv-c") }
        }));
        return configurationService;
    }

    private async Task WriteStatusAsync(string server, ScheduledTaskState? tasks, DateTime? lastUpdate = null)
    {
        Directory.CreateDirectory(_tempDirectory);
        var status = new RuntimeStatus
        {
            Server = server,
            Service = "api",
            LastUpdate = lastUpdate ?? DateTime.UtcNow,
            Status = ServiceStatus.Healthy,
            ScheduledTasks = tasks
        };
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, $"{server}.api.json"), JsonSerializer.Serialize(status, StatusJsonOptions));
    }

    private static ScheduledTaskState Tasks(bool enabled) => new()
    {
        LastChecked = DateTime.UtcNow,
        Tasks = { new ScheduledTaskInfo { Path = Export, Enabled = enabled, State = enabled ? "Ready" : "Disabled" } }
    };

    private string ConfigDirectory => Path.Combine(_tempDirectory, "config");

    private StatusReaderService CreateReader(ConfigurationService config, out AcknowledgeService acknowledgeService)
    {
        acknowledgeService = new AcknowledgeService(_tempDirectory, new Mock<ILogger<AcknowledgeService>>().Object);
        return new StatusReaderService(_tempDirectory, acknowledgeService, config, new Mock<ILogger<StatusReaderService>>().Object,
            stateDirectory: ConfigDirectory);
    }

    [Fact]
    public async Task Reader_AppliesCentralIgnoreRules_AndLowersEventProblem()
    {
        var state = Tasks(true);
        state.EventStatus = MetricStatus.Warning;
        state.EventCount = 1;
        state.RecentEvents.Add(new ScheduledTaskEvent { EventId = 103, TaskPath = Export, Message = "Bekanntes Rauschen" });
        await WriteStatusAsync("srv-a", state);
        await WriteStatusAsync("srv-b", Tasks(false));
        await WriteStatusAsync("srv-c", Tasks(false));
        var reader = CreateReader(CreateConfig(new[] { new { taskPath = Export, eventIds = new[] { 103 }, servers = new[] { "srv-a" }, reason = "bekannt" } }), out _);

        var server = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a");

        server.ScheduledTasks!.RecentEvents.Should().BeEmpty();
        server.ScheduledTasks.IgnoredEventCount.Should().Be(1);
        server.ScheduledTasks.EventStatus.Should().Be(MetricStatus.Healthy);
        server.ActiveProblems.Should().BeEmpty();
        server.OverallStatus.Should().Be(ServiceStatus.Healthy); // no metrics, but task data → not Unknown
    }

    [Fact]
    public async Task Reader_IgnoreRuleForOtherServer_KeepsEventProblem()
    {
        var state = Tasks(true);
        state.EventStatus = MetricStatus.Warning;
        state.EventCount = 1;
        state.RecentEvents.Add(new ScheduledTaskEvent { EventId = 103, TaskPath = Export, Message = "x" });
        await WriteStatusAsync("srv-a", state);
        var reader = CreateReader(CreateConfig(new[] { new { eventIds = new[] { 103 }, servers = new[] { "srv-b" }, reason = "nur b" } }), out _);

        var server = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a");

        server.ActiveProblems.Should().ContainSingle(p => p.Type == "schtask" && p.Severity == ProblemSeverity.Warning);
        server.OverallStatus.Should().Be(ServiceStatus.Degraded);
    }

    [Fact]
    public async Task Reader_MultipleActive_IsAnchoredAtFirstMember_AndCanBeAcknowledgedThere()
    {
        await WriteStatusAsync("srv-a", Tasks(false));
        await WriteStatusAsync("srv-b", Tasks(true));
        await WriteStatusAsync("srv-c", Tasks(true));
        var reader = CreateReader(CreateConfig(), out var acknowledgeService);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);
        var anchor = servers.Single(s => s.ServerId == "srv-a");
        var problem = anchor.ActiveProblems.Should().ContainSingle(p => p.Id.EndsWith("multiactive")).Subject;
        anchor.OverallStatus.Should().Be(ServiceStatus.Unhealthy);
        servers.Where(s => s.ServerId != "srv-a").Should().OnlyContain(s => s.ActiveProblems.Count == 0);

        await acknowledgeService.CreateAcknowledgeAsync("srv-a", problem.Id, "tester", "Umstellung läuft", 60);
        anchor = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a");

        anchor.ActiveProblems.Should().ContainSingle(p => p.Id == problem.Id && p.Acknowledged);
        anchor.OverallStatus.Should().Be(ServiceStatus.Acknowledged);
    }

    [Fact]
    public async Task Reader_GroupProblemSince_IsPersistedAndKeptAcrossReads()
    {
        var firstUpdate = DateTime.UtcNow.AddMinutes(-3);
        await WriteStatusAsync("srv-a", Tasks(true), firstUpdate);
        await WriteStatusAsync("srv-b", Tasks(true), firstUpdate);
        await WriteStatusAsync("srv-c", Tasks(false), firstUpdate);
        var reader = CreateReader(CreateConfig(), out _);

        var first = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a")
            .ActiveProblems.Single(p => p.Id.EndsWith("multiactive"));
        first.Since.Should().BeCloseTo(firstUpdate, TimeSpan.FromSeconds(1));
        File.Exists(Path.Combine(ConfigDirectory, ScheduledTaskGroupSinceStore.FileName)).Should().BeTrue();
        File.Exists(Path.Combine(_tempDirectory, ScheduledTaskGroupSinceStore.FileName)).Should().BeFalse();

        var laterUpdate = DateTime.UtcNow;
        await WriteStatusAsync("srv-a", Tasks(true), laterUpdate);
        await WriteStatusAsync("srv-b", Tasks(true), laterUpdate);
        await WriteStatusAsync("srv-c", Tasks(false), laterUpdate);

        var second = (await CreateReader(CreateConfig(), out _).GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a")
            .ActiveProblems.Single(p => p.Id.EndsWith("multiactive"));
        second.Since.Should().BeCloseTo(firstUpdate, TimeSpan.FromSeconds(1));
        second.LastOccurrence.Should().BeCloseTo(laterUpdate, TimeSpan.FromSeconds(1));
    }

    // ── ScheduledTaskGroupSinceStore ────────────────────────────────────────

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private static readonly DateTime T0 = new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

    private static ActiveProblem P(string id) => new() { Id = id };

    [Fact]
    public void SinceStore_ShortGap_KeepsSince()
    {
        var store = new ScheduledTaskGroupSinceStore(_tempDirectory);
        store.Track(new[] { (P("g1"), T0) }, T0, Keep);
        store.Track(Array.Empty<(ActiveProblem, DateTime)>(), T0.AddMinutes(3), Keep); // one read without the problem

        var again = P("g1");
        store.Track(new[] { (again, T0.AddMinutes(6)) }, T0.AddMinutes(6), Keep);

        again.Since.Should().Be(T0);
        again.LastOccurrence.Should().Be(T0.AddMinutes(6));
    }

    [Fact]
    public void SinceStore_PendingId_KeepsSinceThroughALongOutage_WithoutCreatingEntries()
    {
        var store = new ScheduledTaskGroupSinceStore(_tempDirectory);
        store.Track(new[] { (P("g1"), T0) }, T0, Keep);
        for (var m = 5; m <= 60; m += 5)
            store.Track(Array.Empty<(ActiveProblem, DateTime)>(), T0.AddMinutes(m), Keep, new[] { "g1", "never-seen" });

        var back = P("g1");
        store.Track(new[] { (back, T0.AddMinutes(61)) }, T0.AddMinutes(61), Keep);

        back.Since.Should().Be(T0);
        File.ReadAllText(Path.Combine(_tempDirectory, ScheduledTaskGroupSinceStore.FileName)).Should().NotContain("never-seen");
    }

    [Fact]
    public void SinceStore_LongGap_ResetsSince_AndDropsTheEntry()
    {
        var store = new ScheduledTaskGroupSinceStore(_tempDirectory);
        store.Track(new[] { (P("g1"), T0) }, T0, Keep);
        store.Track(Array.Empty<(ActiveProblem, DateTime)>(), T0.AddMinutes(30), Keep);

        File.ReadAllText(Path.Combine(_tempDirectory, ScheduledTaskGroupSinceStore.FileName)).Should().NotContain("g1");

        var again = P("g1");
        store.Track(new[] { (again, T0.AddMinutes(31)) }, T0.AddMinutes(31), Keep);
        again.Since.Should().Be(T0.AddMinutes(31));
    }

    [Fact]
    public void SinceStore_ReaderNotRunningLongerThanKeep_RestartsSince()
    {
        // The problem is still present, but the reader was down longer than keepResolvedFor.
        new ScheduledTaskGroupSinceStore(_tempDirectory).Track(new[] { (P("g1"), T0) }, T0, Keep);

        var later = P("g1");
        new ScheduledTaskGroupSinceStore(_tempDirectory).Track(new[] { (later, T0.AddMinutes(30)) }, T0.AddMinutes(30), Keep);

        later.Since.Should().Be(T0.AddMinutes(30));
    }

    [Fact]
    public void SinceStore_PersistsAcrossInstances_AndWritesOnlyOnChange()
    {
        var path = Path.Combine(_tempDirectory, ScheduledTaskGroupSinceStore.FileName);
        var store = new ScheduledTaskGroupSinceStore(_tempDirectory);
        store.Track(new[] { (P("g1"), T0) }, T0, Keep);
        var marker = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(path, marker);

        // Same content, LastSeen advanced by less than half of Keep → no write.
        store.Track(new[] { (P("g1"), T0.AddMinutes(2)) }, T0.AddMinutes(2), Keep);
        File.GetLastWriteTimeUtc(path).Should().Be(marker);

        // A new process (e.g. IIS recycle) reads the file.
        var fresh = P("g1");
        new ScheduledTaskGroupSinceStore(_tempDirectory).Track(new[] { (fresh, T0.AddMinutes(4)) }, T0.AddMinutes(4), Keep);
        fresh.Since.Should().Be(T0);

        Directory.GetFiles(_tempDirectory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void SinceStore_ApplyOnly_UsesMemoryWithoutMaintenance()
    {
        var store = new ScheduledTaskGroupSinceStore(null);
        store.Track(new[] { (P("g1"), T0) }, T0, Keep);

        var known = P("g1");
        var unknown = P("g2");
        store.Apply(new[] { (known, T0.AddMinutes(1)), (unknown, T0.AddMinutes(1)) });

        known.Since.Should().Be(T0);
        unknown.Since.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void SinceStore_CorruptFileOrUnwritableDirectory_FallsBackToObservedTime()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, ScheduledTaskGroupSinceStore.FileName), "{ kaputt");
        var p = P("g1");
        new ScheduledTaskGroupSinceStore(_tempDirectory).Track(new[] { (p, T0) }, T0, Keep);
        p.Since.Should().Be(T0);

        // A file where the directory should be → writing fails, logged once, no exception.
        var blocked = Path.Combine(_tempDirectory, "blocked");
        File.WriteAllText(blocked, "x");
        var logger = new Mock<ILogger>();
        var store = new ScheduledTaskGroupSinceStore(blocked, logger.Object);
        var q = P("g1");
        var act = () =>
        {
            store.Track(new[] { (q, T0) }, T0, Keep);
            store.Track(new[] { (P("g2"), T0) }, T0, Keep);
        };
        act.Should().NotThrow();
        q.Since.Should().Be(T0);
        logger.Invocations.Count(i => i.Arguments.Count > 0 && i.Arguments[0] is LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task Reader_AnchorUnknown_KeepsAnchorAndAcknowledge()
    {
        await WriteStatusAsync("srv-a", Tasks(false));
        await WriteStatusAsync("srv-b", Tasks(true));
        await WriteStatusAsync("srv-c", Tasks(true));
        var reader = CreateReader(CreateConfig(), out var acknowledgeService);
        var problem = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a")
            .ActiveProblems.Single(p => p.Id.EndsWith("multiactive"));
        await acknowledgeService.CreateAcknowledgeAsync("srv-a", problem.Id, "tester", "", 60);

        // srv-a times out: its status disappears entirely.
        File.Delete(Path.Combine(_tempDirectory, "srv-a.api.json"));
        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        var anchor = servers.Single(s => s.ServerId == "srv-a");
        anchor.ActiveProblems.Should().ContainSingle(p => p.Id == problem.Id && p.Acknowledged);
        anchor.IsTaskGroupAnchorPlaceholder.Should().BeTrue();
        servers.Where(s => s.ServerId != "srv-a").Should().OnlyContain(s => !s.IsTaskGroupAnchorPlaceholder);
        servers.Where(s => s.ServerId != "srv-a").Should().OnlyContain(s => s.ActiveProblems.Count == 0);
    }

    [Fact]
    public async Task Reader_AllMembersUnknown_KeepsGroupProblemSince()
    {
        var first = DateTime.UtcNow.AddMinutes(-2);
        var other = new ScheduledTaskState { LastChecked = first, Tasks = { new ScheduledTaskInfo { Path = @"\Minicon\Other", Enabled = true } } };
        await WriteStatusAsync("srv-a", other, first);
        await WriteStatusAsync("srv-b", Tasks(false), first);
        await WriteStatusAsync("srv-c", Tasks(false), first);
        var reader = CreateReader(CreateConfig(), out _);
        var problem = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a")
            .ActiveProblems.Single(p => p.Id.EndsWith("noneactive"));

        // srv-b and srv-c (all servers that have the task) go stale → no group at all.
        await WriteStatusAsync("srv-b", Tasks(false), DateTime.UtcNow.AddHours(-3));
        await WriteStatusAsync("srv-c", Tasks(false), DateTime.UtcNow.AddHours(-3));
        (await reader.GetAllServersAsync(forceRefresh: true)).SelectMany(s => s.ActiveProblems).Should().NotContain(p => p.Type == "schtask");
        File.ReadAllText(Path.Combine(ConfigDirectory, ScheduledTaskGroupSinceStore.FileName)).Should().Contain(problem.Id);

        await WriteStatusAsync("srv-b", Tasks(false));
        await WriteStatusAsync("srv-c", Tasks(false));
        var back = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a")
            .ActiveProblems.Single(p => p.Id == problem.Id);
        back.Since.Should().Be(problem.Since);
    }

    [Fact]
    public async Task Reader_UnreachablePlaceholderServer_CountsAsUnknown()
    {
        await WriteStatusAsync("srv-a", Tasks(false));
        await WriteStatusAsync("srv-b", Tasks(false));
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "srv-c.erreichbarkeit.json"), JsonSerializer.Serialize(new RuntimeStatus
        {
            Server = "srv-c", Service = "erreichbarkeit", LastUpdate = DateTime.UtcNow, Status = ServiceStatus.Unknown
        }, StatusJsonOptions));
        var reader = CreateReader(CreateConfig(), out _);

        var groups = await reader.GetScheduledTaskGroupsAsync(forceRefresh: true);

        groups.Single().UnknownServers.Should().Equal("srv-c");
        (await reader.GetAllServersAsync()).SelectMany(s => s.ActiveProblems).Should().NotContain(p => p.Type == "schtask");
    }

    [Fact]
    public async Task Reader_NoneActive_IsAnchoredAtFirstServerOfGroup()
    {
        await WriteStatusAsync("srv-c", Tasks(false));
        await WriteStatusAsync("srv-b", Tasks(false));
        await WriteStatusAsync("srv-a", Tasks(false));
        var reader = CreateReader(CreateConfig(), out _);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Single(s => s.ServerId == "srv-a").ActiveProblems.Should().ContainSingle(p => p.Id.EndsWith("noneactive"));
        servers.Where(s => s.ServerId != "srv-a").Should().OnlyContain(s => s.ActiveProblems.Count == 0);
    }
}
