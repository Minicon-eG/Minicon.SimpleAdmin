using FluentAssertions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>Cross-server scheduled task group problems in the central notifier.</summary>
public class ScheduledTaskNotifierTests : IDisposable
{
    private const string Export = @"\Minicon\Export";
    private static readonly DateTime FixedNow = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Servers = { "srv-a", "srv-b", "srv-c" };

    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"simpleadmin_schtask_notif_{Guid.NewGuid():N}");
    private readonly Mock<IHttpStatusReader> _statusReader = new();
    private readonly Mock<ISmtpMailSender> _mailSender = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly NotificationStateStore _stateStore;
    private readonly List<SmtpMailRequest> _sent = new();
    private List<Acknowledge> _acknowledges = new();

    public ScheduledTaskNotifierTests()
    {
        Directory.CreateDirectory(_stateDir);
        _clock.Setup(c => c.UtcNow).Returns(FixedNow);
        _stateStore = new NotificationStateStore(new Mock<ILogger<NotificationStateStore>>().Object);
        _statusReader.Setup(r => r.FetchActiveAcknowledgesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _acknowledges);
        _statusReader.Setup(r => r.FetchNotifyStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotifyState?)null);
        _mailSender.Setup(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SmtpMailRequest, CancellationToken>((r, _) => _sent.Add(r))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    private static string Url(string server) => $"http://{server}.test/status/{server}.status.json";

    private NotificationService BuildService() =>
        new(new Mock<ILogger<NotificationService>>().Object, _statusReader.Object, _stateStore, _mailSender.Object, _clock.Object,
            _stateDir, masterKey: "", instanceId: "NOTIFIER-TEST");

    private static Config BuildConfig(StatusReportConfig? report = null)
    {
        var config = new Config
        {
            Features = new FeaturesConfig
            {
                ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = true },
                Notifications = new NotificationsFeatureConfig
                {
                    Enabled = true,
                    MinSeverity = "Warning",
                    NotifyOnStaleServer = false,
                    StaleThresholdMinutes = 10,
                    StatusUrls = Servers.Select(Url).ToList(),
                    AcknowledgesUrl = "http://monitoring.test/status/acknowledges.json",
                    SmtpRef = "srv-a:probe1",
                    Recipients = "ops@test.de",
                    WebUiBaseUrl = "http://monitoring.test",
                    SendResolvedNotice = true,
                    StatusReport = report ?? new StatusReportConfig { Enabled = false }
                }
            },
            Environments = { ["production"] = new EnvironmentConfig { Services = Servers.ToList() } }
        };
        foreach (var name in Servers)
        {
            config.Servers.Add(new Server
            {
                Name = name,
                Active = true,
                Checks = new ServerChecksConfig
                {
                    ScheduledTasks = new ServerScheduledTasksConfig { Enabled = true, Tasks = { new ScheduledTaskWatchConfig { Pattern = @"\Minicon\*" } } },
                    EmailProbes = name != "srv-a" ? null : new ServerEmailProbesConfig
                    {
                        Enabled = true,
                        Probes = { new EmailProbeConfig { Name = "probe1", Smtp = new SmtpConfig { Host = "localhost", Port = 25, From = "mon@test.de" } } }
                    }
                }
            });
        }
        return config;
    }

    /// <summary>Serves a fresh status (generated "now" at fetch time) unless an explicit time is given.</summary>
    private void Serve(string server, bool enabled, DateTime? generatedAt = null)
        => _statusReader.Setup(r => r.FetchServerStatusAsync(Url(server), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var at = generatedAt ?? _clock.Object.UtcNow;
                return new CentralServerStatus
                {
                    Hostname = server,
                    GeneratedAt = at,
                    Statuses = new List<RuntimeStatus>
                    {
                        new()
                        {
                            Server = server,
                            Service = "api",
                            LastUpdate = at,
                            Status = ServiceStatus.Healthy,
                            ScheduledTasks = new ScheduledTaskState
                            {
                                LastChecked = at,
                                Tasks = { new ScheduledTaskInfo { Path = Export, Enabled = enabled, State = enabled ? "Ready" : "Disabled" } }
                            }
                        }
                    }
                };
            });

    private void ServeOther(string server)
        => _statusReader.Setup(r => r.FetchServerStatusAsync(Url(server), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CentralServerStatus
            {
                Hostname = server,
                GeneratedAt = _clock.Object.UtcNow,
                Statuses = new List<RuntimeStatus>
                {
                    new()
                    {
                        Server = server, Service = "api", LastUpdate = _clock.Object.UtcNow, Status = ServiceStatus.Healthy,
                        ScheduledTasks = new ScheduledTaskState
                        {
                            LastChecked = _clock.Object.UtcNow,
                            Tasks = { new ScheduledTaskInfo { Path = @"\Minicon\Other", Enabled = true, State = "Ready" } }
                        }
                    }
                }
            });

    private void At(TimeSpan offset) => _clock.Setup(c => c.UtcNow).Returns(FixedNow + offset);

    private Task<int> CycleAsync() => BuildService().RunCycleAsync(BuildConfig());

    private const string ResolvedHeader = "== Behoben / Quittiert ==";

    private void Unreachable(string server)
        => _statusReader.Setup(r => r.FetchServerStatusAsync(Url(server), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CentralServerStatus?)null);

    [Fact]
    public async Task MultipleActive_IsMailedOnce_ForTheAnchor()
    {
        Serve("srv-a", false);
        Serve("srv-b", true);
        Serve("srv-c", true);

        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(1);
        var body = _sent.Single().Body;
        body.Should().Contain("ist auf 2 Servern aktiv");
        CountOccurrences(body, "ist auf 2 Servern aktiv").Should().Be(1);
        body.Should().Contain("ackServer=srv-a");

        // Next cycle: nothing new to send.
        At(TimeSpan.FromMinutes(5));
        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(0);
    }

    [Fact]
    public async Task ShrinkingActiveSet_KeepsAnchorAndId_NoResolvedNoticeAndNoNewMail()
    {
        Serve("srv-a", true);
        Serve("srv-b", true);
        Serve("srv-c", true);
        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(1);

        Serve("srv-a", false);
        At(TimeSpan.FromMinutes(5));
        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(0);
        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task AcknowledgedAtAnchor_IsNotMailed()
    {
        Serve("srv-a", false);
        Serve("srv-b", true);
        Serve("srv-c", true);
        var id = ActiveProblem.ScheduledTaskMultipleActive("production", Export, new[] { "srv-b", "srv-c" }).Id;
        _acknowledges = new List<Acknowledge> { Acknowledge.Create("srv-a", id, "tester", "", 60) };

        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(0);
    }

    [Fact]
    public async Task UnreachableSource_CountsAsUnknown_NoNoneActive()
    {
        Serve("srv-a", false);
        Serve("srv-b", false);
        Unreachable("srv-c");

        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(0);
    }

    [Fact]
    public async Task StaleSource_CountsAsUnknown_NoNoneActive()
    {
        Serve("srv-a", false);
        Serve("srv-b", false);
        Serve("srv-c", true, generatedAt: FixedNow.AddHours(-2));

        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(0);
    }

    [Fact]
    public async Task SourceOlderThanNotifierThreshold_ButWithinGetStaleAfter_IsKnown()
    {
        // Worker interval 300 s → GetStaleAfter = 3 × 300 s + 240 s = 19 min; notifier threshold = 10 min.
        var config = BuildConfig();
        config.General = new GeneralSettings { CheckIntervalSeconds = 300 };
        Serve("srv-a", false);
        Serve("srv-b", false);
        Serve("srv-c", false, generatedAt: FixedNow.AddMinutes(-15));

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1); // srv-c counts as known → "none active" is reported
        _sent.Single().Body.Should().Contain("ist auf keinem Server aktiv");
    }

    [Fact]
    public async Task AnchorUnreachable_KeepsAnchorAndAcknowledge()
    {
        Unreachable("srv-a");
        Serve("srv-b", true);
        Serve("srv-c", true);
        var id = ActiveProblem.ScheduledTaskMultipleActive("production", Export, new[] { "srv-b", "srv-c" }).Id;

        (await CycleAsync()).Should().Be(1);
        _sent.Single().Body.Should().Contain("ackServer=srv-a");

        // Acknowledged at the (still unreachable) anchor → exactly one "Behoben / Quittiert" notice ...
        _acknowledges = new List<Acknowledge> { Acknowledge.Create("srv-a", id, "tester", "", 6000) };
        _sent.Clear();
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(1);
        var parts = _sent.Single().Body.Split(ResolvedHeader);
        parts.Should().HaveCount(2);
        parts[0].Should().NotContain("ist auf 2 Servern aktiv");
        parts[1].Should().Contain("ist auf 2 Servern aktiv");

        // ... and no further mail, not even after the reminder interval.
        _sent.Clear();
        At(TimeSpan.FromHours(5));
        (await CycleAsync()).Should().Be(0);
        _sent.Should().BeEmpty();
    }

    // ── Pending ("in der Schwebe"): an outage must neither resolve nor re-open a group problem ──

    [Theory]
    [InlineData("srv-a")] // the anchor
    [InlineData("srv-c")] // another participant
    public async Task NoneActive_ServerOutage_NoResolvedAndNoNewMail(string failing)
    {
        foreach (var s in Servers)
            Serve(s, false);
        (await CycleAsync()).Should().Be(1);
        _sent.Single().Body.Should().Contain("ist auf keinem Server aktiv");

        Unreachable(failing);
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(0);

        Serve(failing, false);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().ContainSingle();
        _sent.Should().NotContain(m => m.Body.Contains(ResolvedHeader));
    }

    [Fact]
    public async Task MultipleActive_OneActiveServerFails_NoResolvedAndNoNewMail()
    {
        Serve("srv-a", false);
        Serve("srv-b", true);
        Serve("srv-c", true);
        (await CycleAsync()).Should().Be(1);

        Unreachable("srv-c"); // only srv-b known active, srv-c unknown → maybe still several
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(0);

        Serve("srv-c", true);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().ContainSingle();
        _sent.Should().NotContain(m => m.Body.Contains(ResolvedHeader));
    }

    [Fact]
    public async Task MultipleActive_BothActiveServersFail_NoResolvedAndNoNewMail()
    {
        Serve("srv-a", false);
        Serve("srv-b", true);
        Serve("srv-c", true);
        (await CycleAsync()).Should().Be(1);

        Unreachable("srv-b"); // srv-a known + disabled, srv-b and srv-c unknown
        Unreachable("srv-c");
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(0);

        Serve("srv-b", true);
        Serve("srv-c", true);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task AllMembersUnknown_NoGroup_EntryIsHeld_NoResolvedAndNoNewMail()
    {
        ServeOther("srv-a"); // anchor, does not have the task
        Serve("srv-b", false);
        Serve("srv-c", false);
        (await CycleAsync()).Should().Be(1);
        _sent.Single().Body.Should().Contain("ist auf keinem Server aktiv").And.Contain("ackServer=srv-a");

        Unreachable("srv-b"); // every server that has the task is unknown → no group at all
        Unreachable("srv-c");
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(0);

        Serve("srv-b", false);
        Serve("srv-c", false);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Acknowledged_ThenPending_ThenBack_SendsNothingMore()
    {
        foreach (var s in Servers)
            Serve(s, false);
        (await CycleAsync()).Should().Be(1);

        var id = ActiveProblem.ScheduledTaskNoneActive("production", Export, Servers).Id;
        _acknowledges = new List<Acknowledge> { Acknowledge.Create("srv-a", id, "tester", "", 6000) };
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(1); // the one "Behoben / Quittiert" notice
        _sent[1].Body.Split(ResolvedHeader)[1].Should().Contain("ist auf keinem Server aktiv");

        Unreachable("srv-c");
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        Serve("srv-c", false);
        At(TimeSpan.FromMinutes(15));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task AcknowledgedFromTheStart_PendingAndBack_SendsNothing()
    {
        var id = ActiveProblem.ScheduledTaskNoneActive("production", Export, Servers).Id;
        _acknowledges = new List<Acknowledge> { Acknowledge.Create("srv-a", id, "tester", "", 6000) };
        foreach (var s in Servers)
            Serve(s, false);
        (await CycleAsync()).Should().Be(0);

        Unreachable("srv-a");
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(0);

        Serve("srv-a", false);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RealResolution_WhileAllKnown_SendsExactlyOneResolvedNotice()
    {
        foreach (var s in Servers)
            Serve(s, false);
        (await CycleAsync()).Should().Be(1);

        Serve("srv-b", true); // fixed: exactly one active
        At(TimeSpan.FromMinutes(5));
        (await CycleAsync()).Should().Be(1);
        At(TimeSpan.FromMinutes(10));
        (await CycleAsync()).Should().Be(0);

        _sent.Should().HaveCount(2);
        var resolved = _sent[1].Body.Split(ResolvedHeader);
        resolved.Should().HaveCount(2);
        resolved[1].Should().Contain("ist auf keinem Server aktiv");
    }

    [Fact]
    public async Task PendingProblem_KeepsFirstSeenAcrossTheOutage()
    {
        foreach (var s in Servers)
            Serve(s, false);
        await CycleAsync();
        // Outage of 30 min — longer than GetStaleAfter (10 min) — with a notifier cycle every 5 min.
        Unreachable("srv-c");
        for (var minutes = 5; minutes <= 30; minutes += 5)
        {
            At(TimeSpan.FromMinutes(minutes));
            await CycleAsync();
        }

        File.ReadAllText(Path.Combine(_stateDir, ScheduledTaskGroupSinceStore.FileName)).Should().Contain("noneactive");
        var problem = ActiveProblem.ScheduledTaskNoneActive("production", Export, Servers);
        new ScheduledTaskGroupSinceStore(_stateDir).Track(
            new[] { (problem, FixedNow.AddMinutes(31)) }, FixedNow.AddMinutes(31), TimeSpan.FromMinutes(10));
        problem.Since.Should().Be(FixedNow);
    }

    [Fact]
    public async Task NoneActive_AllKnown_IsMailed_AndFirstSeenIsPersisted()
    {
        foreach (var s in Servers)
            Serve(s, false);

        (await BuildService().RunCycleAsync(BuildConfig())).Should().Be(1);

        _sent.Single().Body.Should().Contain("ist auf keinem Server aktiv");
        var since = File.ReadAllText(Path.Combine(_stateDir, ScheduledTaskGroupSinceStore.FileName));
        since.Should().Contain("noneactive");
    }

    [Fact]
    public async Task StatusReport_ListsGroupProblem()
    {
        Serve("srv-a", false);
        Serve("srv-b", true);
        Serve("srv-c", true);
        var slot = FixedNow.AddMinutes(-5).ToLocalTime().ToString("HH:mm");

        await BuildService().RunCycleAsync(BuildConfig(new StatusReportConfig { Enabled = true, Times = new List<string> { slot }, PlainTextOnly = true }));

        var report = _sent.Single(m => m.Subject.Contains("Statusbericht"));
        report.Body.Should().Contain("ist auf 2 Servern aktiv");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
