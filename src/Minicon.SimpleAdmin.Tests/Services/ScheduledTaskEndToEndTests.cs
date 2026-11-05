using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.HtmlGenerator;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Pipeline chain of the cross-server single-active check without Windows: worker state → HTTP status JSON
/// written by the real <see cref="StaticHtmlGenerator"/> → pulled over (fake) HTTP by the real
/// <see cref="StatusReaderService"/> (WebUI) and <see cref="HttpStatusReader"/> (notifier) → group problem at the
/// anchor → acknowledge → status. Also covers enums written as strings and as numbers (older files).
/// </summary>
public sealed class ScheduledTaskEndToEndTests : IDisposable
{
    private const string Export = @"\Minicon\Export";
    private const string Pattern = @"\Minicon\*";
    private static readonly string[] Servers = { "srv-a", "srv-b", "srv-c" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", "schtask-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _published = new(StringComparer.OrdinalIgnoreCase);

    public ScheduledTaskEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // ── Pipeline building blocks ────────────────────────────────────────────

    private static ScheduledTaskState WorkerState(bool enabled, ScheduledTaskMode mode = ScheduledTaskMode.SingleActive) => new()
    {
        LastChecked = DateTime.UtcNow,
        PatternMatches = { [Pattern] = 1 },
        Tasks =
        {
            new ScheduledTaskInfo
            {
                Path = Export,
                Name = "Export",
                Enabled = enabled,
                State = enabled ? "Ready" : "Disabled",
                Mode = mode,
                MatchedPattern = Pattern,
                LastRunTime = DateTime.UtcNow.AddHours(-1),
                LastTaskResult = 0,
                NextRunTime = enabled ? DateTime.UtcNow.AddHours(1) : null
            }
        }
    };

    /// <summary>
    /// Publishes the server's status the way the worker does: the real generator writes
    /// <c>wwwroot/status/{server}.status.json</c> with its own serializer options; the file content is served over HTTP.
    /// </summary>
    private async Task PublishAsync(string server, ScheduledTaskState? state, DateTime? lastUpdate = null, Func<string, string>? rewrite = null)
    {
        var wwwroot = Path.Combine(_dir, "www-" + server);
        var generator = new StaticHtmlGenerator(wwwroot, new Mock<ILogger<StaticHtmlGenerator>>().Object);
        var status = new RuntimeStatus
        {
            Server = server,
            Service = "api",
            LastUpdate = lastUpdate ?? DateTime.UtcNow,
            Status = ServiceStatus.Healthy,
            ScheduledTasks = state
        };

        await generator.GenerateAllAsync(new List<RuntimeStatus> { status }, new Config(), server, Array.Empty<Acknowledge>());

        var json = await File.ReadAllTextAsync(Path.Combine(wwwroot, "status", $"{server}.status.json"));
        _published[server] = rewrite == null ? json : rewrite(json);
    }

    private void Unpublish(string server) => _published[server] = null;

    private HttpClient Http() => new(new StubHandler(request =>
    {
        var server = request.RequestUri!.Host.Split('.')[0];
        var url = request.RequestUri.ToString();
        return _published.TryGetValue(server, out var json) && json != null && url.EndsWith($"/status/{server}.status.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }));

    private static string ConfigJson() => JsonSerializer.Serialize(new
    {
        output = new { pullStatusOverHttp = true },
        environments = new { production = new { services = Servers } },
        features = new { scheduledTasks = new { enabled = true } },
        servers = Servers.Select(name => new
        {
            name,
            active = true,
            baseUrl = $"http://{name}.test",
            serviceTypes = new[] { new { type = "api" } },
            checks = new { scheduledTasks = new { enabled = true, tasks = new[] { new { pattern = Pattern } } } }
        })
    });

    private (StatusReaderService Reader, AcknowledgeService Acknowledges) CreateReader()
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        config.LoadFromJson(ConfigJson());
        var statusDirectory = Path.Combine(_dir, "webui-status");
        Directory.CreateDirectory(statusDirectory);
        var acknowledges = new AcknowledgeService(Path.Combine(_dir, "webui-config"), new Mock<ILogger<AcknowledgeService>>().Object);
        var reader = new StatusReaderService(statusDirectory, acknowledges, config, new Mock<ILogger<StatusReaderService>>().Object,
            Http(), stateDirectory: Path.Combine(_dir, "webui-config"));
        return (reader, acknowledges);
    }

    private static async Task<List<ActiveProblem>> GroupProblemsAsync(StatusReaderService reader)
        => (await reader.GetAllProblemsAsync(forceRefresh: true))
            .Where(p => p.Problem.Type == "schtask")
            .Select(p => p.Problem)
            .ToList();

    /// <summary>Notifier side: same HTTP JSON through the notifier's reader and the shared evaluator.</summary>
    private async Task<Dictionary<string, List<ActiveProblem>>> NotifierGroupProblemsAsync()
    {
        var config = JsonSerializer.Deserialize<Config>(ConfigJson(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var httpReader = new HttpStatusReader(Http(), new Mock<ILogger<HttpStatusReader>>().Object);
        var statuses = new List<RuntimeStatus>();
        foreach (var server in Servers)
        {
            var central = await httpReader.FetchServerStatusAsync($"http://{server}.test/status/{server}.status.json");
            if (central != null)
                statuses.AddRange(central.Statuses);
        }

        var snapshots = ScheduledTaskGroupEvaluator.SnapshotsFromStatuses(statuses, DateTime.UtcNow, ScheduledTaskGroupEvaluator.GetStaleAfter(config));
        return ScheduledTaskGroupEvaluator.DeriveGroupProblems(ScheduledTaskGroupEvaluator.BuildGroups(config, snapshots));
    }

    /// <summary>Old files: the mode as number, or camelCase string from a writer with a naming policy.</summary>
    private static Func<string, string>? ModeEncoding(string encoding) => encoding switch
    {
        "number" => json => Regex.Replace(json, "\"Mode\":\\s*\"(SingleActive|AllServers)\"",
            m => $"\"Mode\": {(m.Groups[1].Value == "SingleActive" ? 0 : 1)}"),
        "camel" => json => Regex.Replace(json, "\"Mode\":\\s*\"(SingleActive|AllServers)\"",
            m => $"\"Mode\": \"{char.ToLowerInvariant(m.Groups[1].Value[0])}{m.Groups[1].Value[1..]}\""),
        _ => null
    };

    // ── Scenarios ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("camel")]
    public async Task TwoActive_YieldOneMultipleActiveProblemAtAnchor_AcknowledgeMakesItAcknowledged_DisablingOneClearsIt(string encoding)
    {
        var rewrite = ModeEncoding(encoding);
        await PublishAsync("srv-a", WorkerState(false), rewrite: rewrite);
        await PublishAsync("srv-b", WorkerState(true), rewrite: rewrite);
        await PublishAsync("srv-c", WorkerState(true), rewrite: rewrite);
        if (encoding == "number")
            _published["srv-b"].Should().Contain("\"Mode\": 0").And.NotContain("SingleActive");

        var (reader, acknowledges) = CreateReader();

        // 1. Exactly one "several active" problem, at the anchor (alphabetically first participant), Critical.
        var problems = await GroupProblemsAsync(reader);
        var problem = problems.Should().ContainSingle().Subject;
        problem.Id.Should().EndWith("_multiactive");
        problem.Severity.Should().Be(ProblemSeverity.Critical);
        problem.Message.Should().Contain("srv-b").And.Contain("srv-c").And.NotContain("srv-a");
        var all = await reader.GetAllServersAsync(forceRefresh: true);
        all.Single(s => s.ServerId == "srv-a").ActiveProblems.Should().ContainSingle(p => p.Id == problem.Id);
        all.Single(s => s.ServerId == "srv-a").OverallStatus.Should().Be(ServiceStatus.Unhealthy);
        all.Where(s => s.ServerId != "srv-a").Should().OnlyContain(s => s.ActiveProblems.Count == 0 && s.OverallStatus == ServiceStatus.Healthy);
        all.Single(s => s.ServerId == "srv-b").ScheduledTasks!.Tasks.Single().Mode.Should().Be(ScheduledTaskMode.SingleActive);

        // Notifier side agrees (same JSON, its own deserializer).
        var notifier = await NotifierGroupProblemsAsync();
        notifier.Should().ContainKey("srv-a").WhoseValue.Should().ContainSingle(p => p.Id == problem.Id);
        notifier.Keys.Should().Equal("srv-a");

        // 2. Acknowledge at the anchor → Acknowledged.
        await acknowledges.CreateAcknowledgeAsync("srv-a", problem.Id, "tester", "Umstellung läuft", 60);
        var anchor = (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a");
        anchor.ActiveProblems.Should().ContainSingle(p => p.Id == problem.Id && p.Acknowledged);
        anchor.OverallStatus.Should().Be(ServiceStatus.Acknowledged);

        // 3. One server disabled the task → exactly one active → no group problem, anchor healthy again.
        await PublishAsync("srv-c", WorkerState(false), rewrite: rewrite);
        (await GroupProblemsAsync(reader)).Should().BeEmpty();
        (await reader.GetAllServersAsync(forceRefresh: true)).Should().OnlyContain(s => s.OverallStatus == ServiceStatus.Healthy);
        (await NotifierGroupProblemsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    public async Task NoneActive_WithAllServersKnown_YieldsNoneActiveProblem(string encoding)
    {
        foreach (var server in Servers)
            await PublishAsync(server, WorkerState(false), rewrite: ModeEncoding(encoding));
        var (reader, _) = CreateReader();

        var problem = (await GroupProblemsAsync(reader)).Should().ContainSingle().Subject;

        problem.Id.Should().EndWith("_noneactive");
        problem.Severity.Should().Be(ProblemSeverity.Critical);
        (await reader.GetAllServersAsync(forceRefresh: true)).Single(s => s.ServerId == "srv-a").ActiveProblems
            .Should().ContainSingle(p => p.Id == problem.Id);
        (await NotifierGroupProblemsAsync()).Should().ContainKey("srv-a").WhoseValue.Should().ContainSingle(p => p.Id == problem.Id);
    }

    [Fact]
    public async Task NoneActive_WithOneServerUnreachable_YieldsNoProblem()
    {
        await PublishAsync("srv-a", WorkerState(false));
        await PublishAsync("srv-b", WorkerState(false));
        Unpublish("srv-c"); // HTTP 404 → placeholder → unknown, might have the task enabled
        var (reader, _) = CreateReader();

        (await GroupProblemsAsync(reader)).Should().BeEmpty();
        (await reader.GetScheduledTaskGroupsAsync(forceRefresh: true)).Single().UnknownServers.Should().Equal("srv-c");
        (await NotifierGroupProblemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoneActive_WithOneServerStale_YieldsNoProblem()
    {
        await PublishAsync("srv-a", WorkerState(false));
        await PublishAsync("srv-b", WorkerState(false));
        await PublishAsync("srv-c", WorkerState(false), lastUpdate: DateTime.UtcNow.AddHours(-2)); // worker stopped
        var (reader, _) = CreateReader();

        (await GroupProblemsAsync(reader)).Should().BeEmpty();
        (await reader.GetScheduledTaskGroupsAsync(forceRefresh: true)).Single().UnknownServers.Should().Equal("srv-c");
        (await NotifierGroupProblemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoneActive_WithOneQueryFailed_YieldsOnlyTheQueryErrorOfThatServer()
    {
        await PublishAsync("srv-a", WorkerState(false));
        await PublishAsync("srv-b", WorkerState(false));
        await PublishAsync("srv-c", new ScheduledTaskState { LastChecked = DateTime.UtcNow, QueryFailed = true, Error = "Zugriff verweigert" });
        var (reader, _) = CreateReader();

        var problems = await reader.GetAllProblemsAsync(forceRefresh: true);

        problems.Should().ContainSingle();
        problems[0].ServerId.Should().Be("srv-c");
        problems[0].Problem.Id.Should().Be(ActiveProblem.ScheduledTaskQueryError("x").Id);
    }

    [Fact]
    public async Task AllServersMode_AsNumber_IsHonoured_NoGroupProblem()
    {
        foreach (var server in Servers)
            await PublishAsync(server, WorkerState(true, ScheduledTaskMode.AllServers), rewrite: ModeEncoding("number"));
        _published["srv-a"].Should().Contain("\"Mode\": 1");
        var (reader, _) = CreateReader();

        (await GroupProblemsAsync(reader)).Should().BeEmpty();
        (await reader.GetScheduledTaskGroupsAsync(forceRefresh: true)).Single().Mode.Should().Be(ScheduledTaskMode.AllServers);
        (await NotifierGroupProblemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task HttpJson_DoesNotCarryTheWorkerOnlyCarriedTasks()
    {
        var state = WorkerState(true);
        state.CarriedTasks = new List<ScheduledTaskInfo> { new() { Path = @"\Minicon\Alt" } };

        await PublishAsync("srv-a", state);

        _published["srv-a"].Should().NotContain(@"\\Minicon\\Alt").And.Contain(@"\\Minicon\\Export");
        state.CarriedTasks.Should().ContainSingle(); // original untouched
    }

    [Fact]
    public async Task ApiActivateExclusive_FedByTheRealReader_UsesGroupServersIncludingUnknown()
    {
        await PublishAsync("srv-a", WorkerState(true));
        await PublishAsync("srv-b", WorkerState(true));
        Unpublish("srv-c");
        var (reader, _) = CreateReader();
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        config.LoadFromJson(ConfigJson());
        IReadOnlyList<string>? passed = null;
        IReadOnlyCollection<string>? reporting = null;
        var control = new Mock<IScheduledTaskControl>();
        control.SetupGet(c => c.IsEnabled).Returns(true);
        control.Setup(c => c.ActivateExclusivelyAsync(Export, "srv-b", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, string, IReadOnlyCollection<string>?, CancellationToken>((_, _, servers, _, rep, _) => { passed = servers; reporting = rep; })
            .ReturnsAsync(new ScheduledTaskExclusiveResult(Export, "srv-b", true, Array.Empty<ScheduledTaskActionResult>(), "OK"));
        var controller = new ScheduledTasksController(control.Object, config, reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = Export.ToLowerInvariant(), TargetServer = "srv-b" }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        passed.Should().Equal("srv-a", "srv-b", "srv-c");
        reporting.Should().BeEquivalentTo(new[] { "srv-a", "srv-b" });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
