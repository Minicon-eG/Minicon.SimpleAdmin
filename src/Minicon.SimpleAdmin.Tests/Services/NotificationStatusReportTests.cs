using FluentAssertions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Moq;
using StatusMetricStatus = Minicon.SimpleAdmin.Models.Status.MetricStatus;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>Scheduled status report of the central notifier (features.notifications.statusReport).</summary>
public class NotificationStatusReportTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"simpleadmin_report_{Guid.NewGuid():N}");
    private readonly Mock<IHttpStatusReader> _statusReader = new();
    private readonly Mock<ISmtpMailSender> _mailSender = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly NotificationStateStore _stateStore;
    private readonly List<SmtpMailRequest> _sent = new();

    private static readonly DateTime FixedNow = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Url1 = "http://server1.test/status/server1.status.json";
    private const string Url2 = "http://server2.test/status/server2.status.json";

    // Report slot 5 minutes before "now", in the machine's local time zone (the notifier uses TimeZoneInfo.Local).
    private static string SlotMinutesAgo(int minutes) => FixedNow.AddMinutes(-minutes).ToLocalTime().ToString("HH:mm");

    public NotificationStatusReportTests()
    {
        Directory.CreateDirectory(_stateDir);
        _clock.Setup(c => c.UtcNow).Returns(FixedNow);
        _stateStore = new NotificationStateStore(new Mock<ILogger<NotificationStateStore>>().Object);

        _statusReader.Setup(r => r.FetchServerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Healthy());
        _statusReader.Setup(r => r.FetchActiveAcknowledgesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Acknowledge>());
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

    private NotificationService BuildService() =>
        new(new Mock<ILogger<NotificationService>>().Object, _statusReader.Object, _stateStore, _mailSender.Object, _clock.Object,
            _stateDir, masterKey: "", instanceId: "NOTIFIER-TEST");

    private static CentralServerStatus Healthy() => new() { GeneratedAt = FixedNow, Statuses = new List<RuntimeStatus>() };

    private static CentralServerStatus CpuCritical(string server) => new()
    {
        GeneratedAt = FixedNow,
        Statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = server,
                Service = "default",
                Status = ServiceStatus.Unhealthy,
                Metrics = new List<Metric>
                {
                    new() { Name = "cpu", Status = StatusMetricStatus.Critical, Value = 99, Threshold = new MetricThreshold { Critical = 90 } }
                }
            }
        }
    };

    private static Config BuildConfig(params string[] times) => new()
    {
        Features = new FeaturesConfig
        {
            Notifications = new NotificationsFeatureConfig
            {
                Enabled = true,
                StatusUrls = new List<string> { Url1, Url2 },
                AcknowledgesUrl = "http://monitoring.test/status/acknowledges.json",
                SmtpRef = "server1:probe1",
                Recipients = "ops@test.de",
                WebUiBaseUrl = "http://monitoring.test",
                StatusReport = new StatusReportConfig { Enabled = true, Times = times.ToList(), PlainTextOnly = false }
            }
        },
        Servers = new List<Server>
        {
            new()
            {
                Name = "server1",
                Active = true,
                Checks = new ServerChecksConfig
                {
                    EmailProbes = new ServerEmailProbesConfig
                    {
                        Enabled = true,
                        Probes = new List<EmailProbeConfig>
                        {
                            new() { Name = "probe1", Smtp = new SmtpConfig { Host = "localhost", Port = 25, From = "mon@test.de" } }
                        }
                    }
                }
            }
        }
    };

    [Fact]
    public async Task AllHealthy_AtSlot_SendsAllOkReport()
    {
        var sent = await BuildService().RunCycleAsync(BuildConfig(SlotMinutesAgo(5)));

        sent.Should().Be(1);
        var mail = _sent.Single();
        mail.Subject.Should().Contain("Statusbericht").And.Contain("alles OK").And.Contain("2 Server");
        mail.Body.Should().Contain("ALLES OK");
        mail.HtmlBody.Should().Contain("Alles OK");
    }

    [Fact]
    public async Task ProblemOnOneServer_ListsAffectedAndOkServers()
    {
        _statusReader.Setup(r => r.FetchServerStatusAsync(Url2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CpuCritical("server2"));

        await BuildService().RunCycleAsync(BuildConfig(SlotMinutesAgo(5)));

        // Problem mail (event-driven) + status report.
        var report = _sent.Single(m => m.Subject.Contains("Statusbericht"));
        report.Subject.Should().Contain("1 von 2 Servern mit Problemen").And.Contain("1 kritisch");
        report.Body.Should().Contain("== server2 ==").And.Contain("Ohne Befund (1): server1");
        report.HtmlBody.Should().Contain("server2").And.Contain("ackServer=server2");
    }

    [Fact]
    public async Task UnreachableServer_IsReported_EvenWhenStaleAlarmsDisabled()
    {
        _statusReader.Setup(r => r.FetchServerStatusAsync(Url2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CentralServerStatus?)null);
        var config = BuildConfig(SlotMinutesAgo(5));
        config.Features!.Notifications.NotifyOnStaleServer = false;

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1); // only the report, no stale alarm
        _sent.Single().Body.Should().Contain("server2").And.Contain("nicht erreichbar");
    }

    [Fact]
    public async Task SecondRunAfterSlot_DoesNotSendAgain()
    {
        var config = BuildConfig(SlotMinutesAgo(5));
        await BuildService().RunCycleAsync(config);

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
        _sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task PeerAlreadySentForSlot_Skips()
    {
        const string peerUrl = "http://peer.test/status/notify-state.json";
        var config = BuildConfig(SlotMinutesAgo(5));
        config.Features!.Notifications.PeerNotifyStateUrls = new List<string> { peerUrl };
        _statusReader.Setup(r => r.FetchNotifyStateAsync(peerUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotifyState { LastReportAttemptAt = FixedNow.AddMinutes(-2), LastReportSentAt = FixedNow.AddMinutes(-2) });

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
    }

    [Fact]
    public async Task SlotOutsideCatchUpWindow_Skips()
    {
        var config = BuildConfig(SlotMinutesAgo(90));
        config.Features!.Notifications.StatusReport.CatchUpMinutes = 60;

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
    }

    [Fact]
    public async Task Disabled_SendsNoReport()
    {
        var config = BuildConfig(SlotMinutesAgo(5));
        config.Features!.Notifications.StatusReport.Enabled = false;

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
    }

    [Fact]
    public async Task PlainTextOnlyNull_InheritsNotifierSetting()
    {
        var config = BuildConfig(SlotMinutesAgo(5));
        config.Features!.Notifications.StatusReport.PlainTextOnly = null;
        config.Features.Notifications.PlainTextOnly = true;

        await BuildService().RunCycleAsync(config);

        _sent.Single().HtmlBody.Should().BeNull();
    }

    [Fact]
    public async Task FailedSend_IsRetriedAfterRetryWindow()
    {
        var config = BuildConfig(SlotMinutesAgo(30));
        _mailSender.SetupSequence(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"))
            .Returns(Task.CompletedTask);

        (await BuildService().RunCycleAsync(config)).Should().Be(0);

        _clock.Setup(c => c.UtcNow).Returns(FixedNow.AddMinutes(5));
        (await BuildService().RunCycleAsync(config)).Should().Be(0, "retry window (10 min) not elapsed");

        _clock.Setup(c => c.UtcNow).Returns(FixedNow.AddMinutes(11));
        (await BuildService().RunCycleAsync(config)).Should().Be(1);
    }

    [Theory]
    [InlineData(new[] { "07:00", "13:00" }, 2)]
    [InlineData(new[] { "07:00, 13:00;18:30" }, 3)]
    [InlineData(new[] { "7:00", "07:00", "kaputt", "25:00" }, 1)]
    public void ParseReportTimes_AcceptsListsAndSeparators(string[] raw, int expected)
    {
        NotificationService.ParseReportTimes(raw).Should().HaveCount(expected);
    }

    [Fact]
    public void GetLatestReportSlot_BeforeFirstTimeToday_UsesYesterday()
    {
        var tz = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 3, 10, 5, 0, 0, DateTimeKind.Utc);

        var slot = NotificationService.GetLatestReportSlotUtc(
            NotificationService.ParseReportTimes(new[] { "07:00", "13:00" }), now, tz);

        slot.Should().Be(new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void DefaultTimes_Are0700_1300_1700_AndOverriddenByConfig()
    {
        new StatusReportConfig().Times.Should().Equal("07:00", "13:00", "17:00");

        var cfg = System.Text.Json.JsonSerializer.Deserialize<StatusReportConfig>(
            "{\"times\":[\"08:00\"]}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        cfg.Times.Should().Equal("08:00");
    }
}
