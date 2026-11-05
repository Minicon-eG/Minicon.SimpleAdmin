using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Status files of older workers (no <c>ScheduledTasks</c> at all, or an early task state without the later fields
/// EventGroups, PatternMatches, CarriedTasks, …) must be read by every reader without errors and must not create
/// scheduled task problems — with the feature on, including central ignore rules.
/// </summary>
public sealed class ScheduledTaskBackwardCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", "schtask-compat-" + Guid.NewGuid().ToString("N"));

    public ScheduledTaskBackwardCompatibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static string Iso(DateTime t) => t.ToString("O");

    /// <summary>RuntimeStatus as written by a v2.36.x worker (RuntimeStatusStore: PascalCase, enums as numbers).</summary>
    private static string OldRuntimeStatus(string server, DateTime lastUpdate) => $$"""
        {
          "Server": "{{server}}",
          "Service": "api",
          "LastUpdate": "{{Iso(lastUpdate)}}",
          "Status": 0,
          "Metrics": [],
          "History": [],
          "Endpoints": [],
          "Services": { "W3SVC": { "Name": "W3SVC", "DisplayName": "WWW", "Status": "Running", "IsHealthy": true, "StartupType": "Automatic" } },
          "ProblemsSince": {}
        }
        """;

    /// <summary>RuntimeStatus with an early scheduled task state: only the first-version fields.</summary>
    private static string EarlyTaskStatus(string server, DateTime lastUpdate, bool enabled) => $$"""
        {
          "Server": "{{server}}",
          "Service": "api",
          "LastUpdate": "{{Iso(lastUpdate)}}",
          "Status": 0,
          "ScheduledTasks": {
            "LastChecked": "{{Iso(lastUpdate)}}",
            "QueryFailed": false,
            "Tasks": [
              { "Path": "\\Minicon\\Export", "Name": "Export", "Enabled": {{(enabled ? "true" : "false")}}, "State": "{{(enabled ? "Ready" : "Disabled")}}",
                "LastTaskResult": 0, "Mode": 0 }
            ],
            "RecentEvents": [ { "Timestamp": "{{Iso(lastUpdate)}}", "EventId": 103, "Level": "Error", "TaskPath": "\\Minicon\\Export", "Message": "x" } ],
            "EventCount": 1,
            "EventStatus": 0
          }
        }
        """;

    private static ConfigurationService Config(bool featureEnabled, params string[] servers)
    {
        var service = new ConfigurationService(new ConfigurationBuilder().Build());
        service.LoadFromJson(JsonSerializer.Serialize(new
        {
            output = new { pullStatusOverHttp = false },
            environments = new { production = new { services = servers } },
            features = new
            {
                scheduledTasks = new
                {
                    enabled = featureEnabled,
                    ignoreRules = new[] { new { taskPath = @"\Minicon\*", eventIds = new[] { 103 }, reason = "alt" } }
                },
                eventLog = new { ignoreRules = new[] { new { source = "DCOM", reason = "alt" } } }
            },
            servers = servers.Select(name => new
            {
                name,
                active = true,
                serviceTypes = new[] { new { type = "api" } },
                checks = new { scheduledTasks = new { enabled = true, tasks = new[] { new { pattern = @"\Minicon\*" } } } }
            })
        }));
        return service;
    }

    private StatusReaderService Reader(ConfigurationService config)
        => new(_dir, new AcknowledgeService(Path.Combine(_dir, "ack"), new Mock<ILogger<AcknowledgeService>>().Object), config,
            new Mock<ILogger<StatusReaderService>>().Object, stateDirectory: Path.Combine(_dir, "state"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebUi_OldStatusWithoutScheduledTasks_IsRead_AndYieldsNoProblems(bool featureEnabled)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-old.api.json"), OldRuntimeStatus("srv-old", DateTime.UtcNow));
        var reader = Reader(Config(featureEnabled, "srv-old"));

        var server = (await reader.GetAllServersAsync(forceRefresh: true)).Should().ContainSingle().Subject;

        server.ServerId.Should().Be("srv-old");
        server.ScheduledTasks.Should().BeNull();
        server.ActiveProblems.Should().BeEmpty();
        server.OverallStatus.Should().Be(ServiceStatus.Healthy);
        server.WindowsServices.Should().ContainKey("W3SVC");
        (await reader.GetScheduledTaskGroupsAsync(forceRefresh: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task WebUi_MixedOldAndNewWorkers_OldServerIsUnknown_NoNoneActiveProblem()
    {
        // srv-b still runs an older worker: it may have the task enabled, so "none active" must not be reported.
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-a.api.json"), EarlyTaskStatus("srv-a", DateTime.UtcNow, enabled: false));
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-b.api.json"), OldRuntimeStatus("srv-b", DateTime.UtcNow));
        var reader = Reader(Config(true, "srv-a", "srv-b"));

        var problems = await reader.GetAllProblemsAsync(forceRefresh: true);

        problems.Should().BeEmpty();
        (await reader.GetScheduledTaskGroupsAsync(forceRefresh: true)).Single().UnknownServers.Should().Equal("srv-b");
    }

    [Fact]
    public async Task WebUi_EarlyTaskStateWithoutNewFields_IsRead_LegacyIgnorePath_NoProblems()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-a.api.json"), EarlyTaskStatus("srv-a", DateTime.UtcNow, enabled: true));
        var reader = Reader(Config(true, "srv-a"));

        var server = (await reader.GetAllServersAsync(forceRefresh: true)).Single();

        var state = server.ScheduledTasks!;
        state.EventGroups.Should().BeNull();
        state.PatternMatches.Should().BeEmpty();   // no "pattern without hits" for old files
        state.CarriedTasks.Should().BeNull();
        state.Tasks.Single().Mode.Should().Be(ScheduledTaskMode.SingleActive);
        state.Tasks.Single().RemediationSteps.Should().BeEmpty();
        state.RecentEvents.Should().BeEmpty();     // removed by the central rule (legacy path without groups)
        state.EventCount.Should().Be(0);
        server.ActiveProblems.Should().BeEmpty();
        server.OverallStatus.Should().Be(ServiceStatus.Healthy);
    }

    [Fact]
    public async Task Notifier_OldStatusWithoutScheduledTasks_IsParsed_AndYieldsNoProblems()
    {
        var central = $$"""
            { "Hostname": "srv-old", "GeneratedAt": "{{Iso(DateTime.UtcNow)}}", "Statuses": [ {{OldRuntimeStatus("srv-old", DateTime.UtcNow)}} ] }
            """;
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(central, Encoding.UTF8, "application/json")
        }));

        var parsed = await new HttpStatusReader(http, new Mock<ILogger<HttpStatusReader>>().Object)
            .FetchServerStatusAsync("http://srv-old.test/status/srv-old.status.json");

        var status = parsed!.Statuses.Should().ContainSingle().Subject;
        status.ScheduledTasks.Should().BeNull();
        ScheduledTaskIgnoreMatcher.ApplyCentral(status.ScheduledTasks, "srv-old",
            new List<ScheduledTaskIgnoreRule> { new() { EventIds = { 103 } } }, new ScheduledTaskDefaults(), DateTime.UtcNow).Should().Be(0);
        ProblemDerivationService.DeriveProblems(status.Metrics, status.AppPools, status.Services, status.EventLogs,
            status.SqlQueryChecks, status.EmailProbes, status.EmailDelivery, status.Certificates, status.BizTalk,
            status.FileMonitoring, status.ScheduledTasks).Should().BeEmpty();
    }

    [Fact]
    public async Task Worker_OwnOldStatusFile_IsLoaded_AndServesAsPreviousStateWithoutAlarm()
    {
        var now = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        var store = new RuntimeStatusStore(_dir, new Mock<ILogger<RuntimeStatusStore>>().Object);
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-a.api.json"), EarlyTaskStatus("srv-a", now.AddMinutes(-1), enabled: true));
        await File.WriteAllTextAsync(Path.Combine(_dir, "srv-b.api.json"), OldRuntimeStatus("srv-b", now.AddMinutes(-1)));

        var previousEarly = (await store.LoadStatusAsync("srv-a", "api"))!.ScheduledTasks;
        var previousNone = (await store.LoadStatusAsync("srv-b", "api"))!.ScheduledTasks;
        previousEarly.Should().NotBeNull();
        previousNone.Should().BeNull();

        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(new RawScheduledTask
        {
            TaskPath = @"\Minicon\", TaskName = "Export", State = "Ready", Enabled = true,
            LastRunTime = now.AddMinutes(-30), LastTaskResult = 0, NextRunTime = now.AddMinutes(30), NumberOfMissedRuns = 4
        });
        var config = new ServerScheduledTasksConfig { Enabled = true, Tasks = { new ScheduledTaskWatchConfig { Pattern = @"\Minicon\*" } } };

        foreach (var previous in new[] { previousEarly, previousNone })
        {
            var state = ScheduledTaskChecker.Evaluate(config, new ScheduledTaskDefaults(), raw, null, now, previous);
            var task = state.Tasks.Single();
            task.MissedRunsIncreased.Should().BeFalse(); // old file had no counter → no alarm on the first cycle
            task.MissedRun.Should().BeFalse();
            ProblemDerivationService.DeriveProblems(new List<Metric>(), null, scheduledTasks: state).Should().BeEmpty();
        }
    }

    [Fact]
    public void FailedQuery_AfterOldPreviousState_CarriesTheOldTasks()
    {
        var previous = JsonSerializer.Deserialize<RuntimeStatus>(EarlyTaskStatus("srv-a", DateTime.UtcNow, enabled: true),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ScheduledTasks;

        var failed = ScheduledTaskChecker.Failed(previous, DateTime.UtcNow, "Timeout");

        failed.CarriedTasks.Should().ContainSingle(t => t.Path == @"\Minicon\Export");
        failed.Tasks.Should().BeEmpty();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
