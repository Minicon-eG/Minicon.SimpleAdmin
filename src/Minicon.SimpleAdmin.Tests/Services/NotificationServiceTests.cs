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

public class NotificationServiceTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"simpleadmin_notif_{Guid.NewGuid():N}");
    private readonly Mock<ILogger<NotificationService>> _logger = new();
    private readonly Mock<IHttpStatusReader> _statusReader = new();
    private readonly Mock<ISmtpMailSender> _mailSender = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly NotificationStateStore _stateStore;

    private static readonly DateTime FixedNow = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string StatusUrl = "http://server1.test/status/server1.status.json";
    private const string AcksUrl = "http://monitoring.test/status/acknowledges.json";
    private const string StaleProblemId = "server_server1_stale";

    public NotificationServiceTests()
    {
        Directory.CreateDirectory(_stateDir);
        _clock.Setup(c => c.UtcNow).Returns(FixedNow);
        _stateStore = new NotificationStateStore(new Mock<ILogger<NotificationStateStore>>().Object);

        // Defaults: target server unreachable, no acknowledges, no peer state, mail sender succeeds.
        _statusReader.Setup(r => r.FetchServerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CentralServerStatus?)null);
        _statusReader.Setup(r => r.FetchActiveAcknowledgesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Acknowledge>());
        _statusReader.Setup(r => r.FetchNotifyStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotifyState?)null);
        _mailSender.Setup(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    private NotificationService BuildService(string instanceId = "NOTIFIER-TEST") =>
        new(_logger.Object, _statusReader.Object, _stateStore, _mailSender.Object, _clock.Object,
            _stateDir, masterKey: "", instanceId: instanceId);

    private static Config BuildConfig(bool notifyOnStale = true) =>
        new()
        {
            Features = new FeaturesConfig
            {
                Notifications = new NotificationsFeatureConfig
                {
                    Enabled = true,
                    MinSeverity = "Warning",
                    NotifyOnStaleServer = notifyOnStale,
                    StaleThresholdMinutes = 10,
                    ReNotifyIntervalHours = 4,
                    RetryAfterMinutes = 10,
                    StatusUrls = new List<string> { StatusUrl },
                    AcknowledgesUrl = AcksUrl,
                    SmtpRef = "server1:probe1",
                    Recipients = "ops@test.de",
                    WebUiBaseUrl = "http://monitoring.test"
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
                                new()
                                {
                                    Name = "probe1",
                                    Smtp = new SmtpConfig { Host = "localhost", Port = 25, From = "mon@test.de", To = "fallback@test.de" }
                                }
                            }
                        }
                    }
                }
            }
        };

    private async Task SeedOwnStateAsync(NotifyEntry entry)
    {
        var path = Path.Combine(_stateDir, "notify-state.json");
        await _stateStore.SaveAsync(path, new NotifyState
        {
            Entries = new Dictionary<string, NotifyEntry> { [StaleProblemId] = entry }
        });
    }

    [Fact]
    public async Task RunCycle_Disabled_DoesNothing()
    {
        var config = BuildConfig();
        config.Features!.Notifications.Enabled = false;

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycle_StaleServer_SendsMailWithDeepLink()
    {
        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(r =>
            r.Recipients.Contains("ops@test.de") &&
            r.Body.Contains("http://monitoring.test/Problems?ackServer=server1") &&
            r.Body.Contains(StaleProblemId)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_StaleServer_SendsMultipartMailWithHtmlBody()
    {
        // HTML ist Opt-in (Default: Nur-Text) — hier explizit aktivieren.
        var config = BuildConfig();
        config.Features!.Notifications.PlainTextOnly = false;

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(r =>
            r.HtmlBody != null &&
            r.HtmlBody!.StartsWith("<!DOCTYPE html>") &&
            r.HtmlBody.Contains("SimpleAdmin Monitoring") &&
            r.HtmlBody.Contains("Problems?ackServer=server1") &&
            // plain-text alternative is still present for non-HTML clients
            r.Body.Contains("server1")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_DefaultConfig_SendsPlainTextWithoutHtmlBody()
    {
        // Sicherer Default: Nur-Text-Mails — manche Mail-Filter verschlucken
        // multipart/HTML nach der SMTP-Annahme. HTML ist Opt-in (PlainTextOnly=false).
        var config = BuildConfig();
        config.Features!.Notifications.PlainTextOnly.Should().BeTrue("Plaintext muss der Default sein");

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(r =>
            string.IsNullOrEmpty(r.HtmlBody) &&
            r.Body.Contains("server1")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_WithWwwroot_PublishesNotifyStateCopy()
    {
        // Der Peer liest den State per HTTP aus {wwwroot}/status — die Kopie wird
        // bei jedem Save automatisch publiziert (kein localNotifyStatePath noetig).
        var wwwroot = Path.Combine(_stateDir, "wwwroot");
        var service = new NotificationService(
            _logger.Object, _statusReader.Object, _stateStore, _mailSender.Object, _clock.Object,
            _stateDir, masterKey: "", instanceId: "NOTIFIER-TEST", wwwrootDirectory: wwwroot);

        await service.RunCycleAsync(BuildConfig());

        File.Exists(Path.Combine(wwwroot, "status", "notify-state.json")).Should().BeTrue();
        var published = await _stateStore.LoadAsync(Path.Combine(wwwroot, "status", "notify-state.json"));
        published.Entries.Keys.Should().Contain(k => k.Contains(StaleProblemId));
    }

    [Fact]
    public async Task RunCycle_RendererIndependent_MailFailureDoesNotThrow()
    {
        // Ein Fehler beim Senden (oder Rendern) darf den Zyklus nicht abbrechen —
        // der Versuch bleibt geclaimt und wird nach RetryAfterMinutes wiederholt.
        _mailSender.Setup(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP kaputt"));

        var act = async () => await BuildService().RunCycleAsync(BuildConfig());

        var sent = await act.Should().NotThrowAsync();
        sent.Subject.Should().Be(0);
    }

    [Fact]
    public async Task RunCycle_ResolvedProblem_MailStatesExactProblem()
    {
        // notifyOnStale=false → the previously-notified problem is no longer open → all-clear.
        var config = BuildConfig(notifyOnStale: false);
        config.Features!.Notifications.SendResolvedNotice = true;
        config.Features!.Notifications.PlainTextOnly = false;

        await SeedOwnStateAsync(new NotifyEntry
        {
            ServerId = "server1",
            Severity = "Critical",
            Message = "Service 'Druckwarteschlange' ist nicht gestartet",
            FirstSeenAt = FixedNow.AddHours(-2),
            LastAttemptAt = FixedNow.AddHours(-2),
            LastNotifiedAt = FixedNow.AddHours(-2)
        });

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(r =>
            r.Body.Contains("Behoben / Quittiert") &&
            r.Body.Contains("ist nicht gestartet") &&
            r.HtmlBody != null &&
            r.HtmlBody!.Contains("ist nicht gestartet")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_AcknowledgedProblem_DoesNotSend()
    {
        _statusReader.Setup(r => r.FetchActiveAcknowledgesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Acknowledge>
            {
                new() { ServerId = "server1", ProblemId = StaleProblemId, Status = AcknowledgeStatus.Active }
            });

        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(0);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycle_RecentlyNotified_SkipsDuplicate()
    {
        await SeedOwnStateAsync(new NotifyEntry { ServerId = "server1", LastAttemptAt = FixedNow, LastNotifiedAt = FixedNow });

        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(0);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycle_ReminderIntervalElapsed_SendsAgain()
    {
        await SeedOwnStateAsync(new NotifyEntry
        {
            ServerId = "server1",
            LastAttemptAt = FixedNow.AddHours(-5),
            LastNotifiedAt = FixedNow.AddHours(-5) // older than the 4h reminder interval
        });

        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_PeerAlreadyAttempted_SkipsDuplicate()
    {
        var config = BuildConfig();
        const string peerUrl = "http://server2.test/status/notify-state.json";
        config.Features!.Notifications.PeerNotifyStateUrls = new List<string> { peerUrl };

        _statusReader.Setup(r => r.FetchNotifyStateAsync(peerUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotifyState
            {
                Entries = new Dictionary<string, NotifyEntry>
                {
                    [StaleProblemId] = new NotifyEntry { ServerId = "server1", LastAttemptAt = FixedNow, LastNotifiedAt = FixedNow }
                }
            });

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(0);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycle_PeerNotificationForAnotherServer_DoesNotSuppressMail()
    {
        var config = BuildConfig();
        const string peerUrl = "http://server1.test/status/notify-state.json";
        config.Features!.Notifications.PeerNotifyStateUrls = new List<string> { peerUrl };

        _statusReader.Setup(r => r.FetchServerStatusAsync(StatusUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CentralServerStatus
            {
                GeneratedAt = FixedNow,
                Statuses = new List<RuntimeStatus>
                {
                    new()
                    {
                        Server = "server2",
                        Service = "default",
                        Status = ServiceStatus.Unhealthy,
                        Metrics = new List<Metric>
                        {
                            new() { Name = "cpu", Status = StatusMetricStatus.Critical, Value = 99, Threshold = new MetricThreshold { Critical = 90 } }
                        }
                    }
                }
            });
        _statusReader.Setup(r => r.FetchNotifyStateAsync(peerUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotifyState
            {
                Entries = new Dictionary<string, NotifyEntry>
                {
                    ["cpu_usage_critical"] = new() { ServerId = "server1", LastAttemptAt = FixedNow, LastNotifiedAt = FixedNow }
                }
            });

        var sent = await BuildService().RunCycleAsync(config);

        sent.Should().Be(1);
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(r =>
            r.Body.Contains("ackServer=server2") && r.Body.Contains("cpu_usage_critical")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_NotifyOnStaleDisabled_NoMail()
    {
        var sent = await BuildService().RunCycleAsync(BuildConfig(notifyOnStale: false));

        sent.Should().Be(0);
        _mailSender.Verify(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycle_FailedSend_LeavesClaimForRetry()
    {
        _mailSender.Setup(m => m.SendAsync(It.IsAny<SmtpMailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        var sent = await BuildService().RunCycleAsync(BuildConfig());

        sent.Should().Be(0);
        // Claim (LastAttemptAt) was written, but no successful notification.
        var state = await _stateStore.LoadAsync(Path.Combine(_stateDir, "notify-state.json"));
        var stateKey = $"server1|{StaleProblemId}";
        state.Entries.Should().ContainKey(stateKey);
        state.Entries[stateKey].LastAttemptAt.Should().Be(FixedNow);
        state.Entries[stateKey].LastNotifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task RunCycle_NoOverrides_DerivesUrlsFromServerBaseUrls()
    {
        // Two servers with baseUrls, notifierServers + webUiServer set, NO explicit URL overrides.
        // Both status fetches return null → stale → ServerStale problems → a mail is sent.
        var config = BuildDerivedConfig();
        var service = BuildService(instanceId: "server1");

        var sent = await service.RunCycleAsync(config);

        sent.Should().Be(1);
        // statusUrls derived from each active server's baseUrl
        _statusReader.Verify(r => r.FetchServerStatusAsync("https://server1.test/status/server1.status.json", It.IsAny<CancellationToken>()), Times.Once);
        _statusReader.Verify(r => r.FetchServerStatusAsync("https://server2.test/status/server2.status.json", It.IsAny<CancellationToken>()), Times.Once);
        // acknowledgesUrl derived from webUiServer (server1)
        _statusReader.Verify(r => r.FetchActiveAcknowledgesAsync("https://server1.test/status/acknowledges.json", It.IsAny<CancellationToken>()), Times.Once);
        // peer notify-state derived from the OTHER notifier (server2); self (server1) excluded
        _statusReader.Verify(r => r.FetchNotifyStateAsync("https://server2.test/status/notify-state.json", It.IsAny<CancellationToken>()), Times.Once);
        _statusReader.Verify(r => r.FetchNotifyStateAsync("https://server1.test/status/notify-state.json", It.IsAny<CancellationToken>()), Times.Never);
        // deep-link base derived from webUiServer (server1)
        _mailSender.Verify(m => m.SendAsync(It.Is<SmtpMailRequest>(req =>
            req.Body.Contains("https://server1.test/Problems?ackServer=")), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Config BuildDerivedConfig() =>
        new()
        {
            Features = new FeaturesConfig
            {
                Notifications = new NotificationsFeatureConfig
                {
                    Enabled = true,
                    MinSeverity = "Warning",
                    NotifyOnStaleServer = true,
                    NotifierServers = new List<string> { "server1", "server2" },
                    WebUiServer = "server1",
                    SmtpRef = "server1:probe1",
                    Recipients = "ops@test.de"
                    // no StatusUrls / AcknowledgesUrl / PeerNotifyStateUrls / WebUiBaseUrl → all derived
                }
            },
            Servers = new List<Server>
            {
                new()
                {
                    Name = "server1",
                    Active = true,
                    BaseUrl = "https://server1.test",
                    Checks = new ServerChecksConfig
                    {
                        EmailProbes = new ServerEmailProbesConfig
                        {
                            Enabled = true,
                            Probes = new List<EmailProbeConfig>
                            {
                                new()
                                {
                                    Name = "probe1",
                                    Smtp = new SmtpConfig { Host = "localhost", Port = 25, From = "mon@test.de", To = "fallback@test.de" }
                                }
                            }
                        }
                    }
                },
                new() { Name = "server2", Active = true, BaseUrl = "https://server2.test/" }
            }
        };
}
