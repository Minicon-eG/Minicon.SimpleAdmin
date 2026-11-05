using FluentAssertions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.HtmlGenerator;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Moq;
using StateMetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>Scheduled task sections of the static HTML pages.</summary>
public class ScheduledTaskHtmlTests : IDisposable
{
    private const string Export = @"\Minicon\Export";
    private const string Xss = "<script>alert(1)</script>";
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static Config CreateConfig(params string[] servers)
    {
        var config = new Config { Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = true } } };
        foreach (var name in servers)
        {
            config.Servers.Add(new Server
            {
                Name = name,
                Active = true,
                Checks = new ServerChecksConfig
                {
                    ScheduledTasks = new ServerScheduledTasksConfig { Enabled = true, Tasks = { new ScheduledTaskWatchConfig { Pattern = @"\Minicon\*" } } }
                }
            });
        }
        return config;
    }

    private static RuntimeStatus Status(string server, params ScheduledTaskInfo[] tasks) => new()
    {
        Server = server,
        Service = "api",
        LastUpdate = DateTime.UtcNow,
        Status = ServiceStatus.Healthy,
        ScheduledTasks = new ScheduledTaskState { LastChecked = DateTime.UtcNow, Tasks = tasks.ToList() }
    };

    [Fact]
    public void DetailSection_EncodesTaskPathAndEventText_AndHighlightsFailures()
    {
        var state = new ScheduledTaskState
        {
            LastChecked = DateTime.UtcNow,
            Tasks =
            {
                new ScheduledTaskInfo { Path = @"\Minicon\" + Xss, Enabled = true, State = "Ready", LastRunFailed = true, LastTaskResult = unchecked((int)0x80070005) },
                new ScheduledTaskInfo { Path = Export, Enabled = false, State = "Disabled", Mode = ScheduledTaskMode.AllServers }
            },
            EventStatus = StateMetricStatus.Warning,
            EventCount = 1,
            RecentEvents = { new ScheduledTaskEvent { EventId = 103, TaskPath = Export, Message = Xss } }
        };

        var html = StaticHtmlGenerator.GenerateScheduledTasksSectionHtml(state);

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("🕒 Geplante Tasks");
        html.Should().Contain("0x80070005");
        html.Should().Contain("background:#fdecea");
        html.Should().Contain("Alle Server").And.Contain("Ein Server");
    }

    [Fact]
    public void DetailSection_QueryFailed_ShowsEncodedError()
    {
        var html = StaticHtmlGenerator.GenerateScheduledTasksSectionHtml(new ScheduledTaskState { QueryFailed = true, Error = Xss });

        html.Should().Contain("Abfrage fehlgeschlagen").And.NotContain("<script>");
    }

    [Fact]
    public void GroupSection_ShowsViolation_EncodesPath_AndMarksAcknowledged()
    {
        var path = @"\Minicon\" + Xss;
        var statuses = new[]
        {
            Status("srv-a", new ScheduledTaskInfo { Path = path, Enabled = true }),
            Status("srv-b", new ScheduledTaskInfo { Path = path, Enabled = true })
        };
        var config = CreateConfig("srv-a", "srv-b");

        var open = StaticHtmlGenerator.RenderScheduledTaskGroupSection(config, statuses, null, DateTime.UtcNow, "");
        open.Should().Contain("ist auf 2 Servern aktiv").And.NotContain("<script>").And.NotContain("quittiert");

        var id = ActiveProblem.ScheduledTaskMultipleActive(string.Empty, path, new[] { "srv-a", "srv-b" }).Id;
        var acked = StaticHtmlGenerator.RenderScheduledTaskGroupSection(
            config, statuses, new[] { Acknowledge.Create("srv-a", id, "tester", "", 60) }, DateTime.UtcNow, "");
        acked.Should().Contain("(quittiert)");
    }

    [Fact]
    public void GroupSection_ListsServersWithoutCurrentState()
    {
        var statuses = new[] { Status("srv-a", new ScheduledTaskInfo { Path = Export, Enabled = false }) };

        var html = StaticHtmlGenerator.RenderScheduledTaskGroupSection(CreateConfig("srv-a", "srv-b"), statuses, null, DateTime.UtcNow, "");

        html.Should().Contain("Server ohne aktuellen Stand (nicht erreichbar, veraltet oder Abfrage fehlgeschlagen): srv-b");
        html.Should().NotContain("auf keinem Server aktiv");
    }

    [Fact]
    public void GroupSection_FeatureDisabled_RendersNothing()
    {
        var config = CreateConfig("srv-a");
        config.Features!.ScheduledTasks.Enabled = false;

        StaticHtmlGenerator.RenderScheduledTaskGroupSection(config, new[] { Status("srv-a", new ScheduledTaskInfo { Path = Export }) }, null, DateTime.UtcNow, "")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task DetailPage_OfAnchor_ShowsGroupProblem_AndItsAcknowledge()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, new Mock<ILogger<StaticHtmlGenerator>>().Object);
        var statuses = new List<RuntimeStatus> { Status("server-a", new ScheduledTaskInfo { Path = Export, Enabled = false, State = "Disabled" }) };
        var id = ActiveProblem.ScheduledTaskNoneActive(string.Empty, Export, new[] { "server-a" }).Id;

        await generator.GenerateAllAsync(statuses, CreateConfig("server-a"), "server-a", new List<Acknowledge>());
        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("ist auf keinem Server aktiv").And.Contain("<p><strong>Status:</strong> Unacknowledged</p>");

        await generator.GenerateAllAsync(statuses, CreateConfig("server-a"), "server-a",
            new List<Acknowledge> { Acknowledge.Create("server-a", id, "tester", "", 60) });
        html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("<p><strong>Status:</strong> Acknowledged</p>");
    }
}
