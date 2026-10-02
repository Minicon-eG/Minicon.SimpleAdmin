using Minicon.SimpleAdmin.HtmlGenerator;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Microsoft.Extensions.Logging;
using Moq;
using StateMetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Tests.Services;

public class StaticHtmlGeneratorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<StaticHtmlGenerator>> _logger = new();

    [Fact]
    public async Task GenerateAllAsync_RendersAcknowledgedProblemsOnServicePage()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Unhealthy,
                Metrics =
                {
                    new Metric
                    {
                        Name = "cpu",
                        DisplayName = "CPU",
                        Unit = "%",
                        Value = 95,
                        Status = Minicon.SimpleAdmin.Models.Status.MetricStatus.Critical,
                        Threshold = new MetricThreshold { Warning = 70, Critical = 90, Operator = ">" }
                    }
                }
            }
        };

        var acknowledges = new List<Acknowledge>
        {
            Acknowledge.Create("server-a", "cpu_usage_critical", "tester", "Investigating", 30)
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", acknowledges);

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("<p><strong>Status:</strong> Acknowledged</p>");
        html.Should().NotContain("<p><strong>Status:</strong> Unacknowledged</p>");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersAppPoolSection_WhenPoolsPresent()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Healthy,
                AppPools = new Dictionary<string, AppPoolState>
                {
                    {
                        "DefaultAppPool", new AppPoolState
                        {
                            Name = "DefaultAppPool",
                            Status = AppPoolStatus.Running,
                            MemoryMB = 350,
                            MemoryStatus = StateMetricStatus.Healthy,
                            UptimeHours = 24,
                            UptimeStatus = StateMetricStatus.Healthy,
                            UptimeWarningHours = 168
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("IIS Application Pools");
        html.Should().Contain("DefaultAppPool");
        html.Should().Contain("Running");
        html.Should().Contain("350");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersUptimeWarning_WhenUptimeStatusIsWarning()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Degraded,
                AppPools = new Dictionary<string, AppPoolState>
                {
                    {
                        "DefaultAppPool", new AppPoolState
                        {
                            Name = "DefaultAppPool",
                            Status = AppPoolStatus.Running,
                            MemoryStatus = StateMetricStatus.Healthy,
                            UptimeHours = 200,
                            UptimeStatus = StateMetricStatus.Warning,
                            UptimeWarningHours = 168
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("200");
        html.Should().Contain("Warning");
        html.Should().Contain("metric-warning");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersStoppedPool_WhenPoolIsStopped()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Unhealthy,
                AppPools = new Dictionary<string, AppPoolState>
                {
                    {
                        "DefaultAppPool", new AppPoolState
                        {
                            Name = "DefaultAppPool",
                            Status = AppPoolStatus.Stopped,
                            StoppedSince = DateTime.UtcNow.AddMinutes(-30)
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("DefaultAppPool");
        html.Should().Contain("Stopped");
        html.Should().Contain("ago");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersEmailProbeSection_WhenProbeIsHealthy()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Healthy,
                EmailProbes = new Dictionary<string, EmailProbeState>
                {
                    {
                        "seg1-relay", new EmailProbeState
                        {
                            ProbeId = "seg1-relay",
                            ProbeName = "Segment 1 SMTP-Relay",
                            Status = StateMetricStatus.Healthy,
                            LastSentAt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                            ConsecutiveFailures = 0
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("E-Mail-Probes (Sender)");
        html.Should().Contain("Segment 1 SMTP-Relay");
        html.Should().Contain("Healthy");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersEmailProbeSection_WithError_WhenProbeIsCritical()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Unhealthy,
                EmailProbes = new Dictionary<string, EmailProbeState>
                {
                    {
                        "seg1-relay", new EmailProbeState
                        {
                            ProbeId = "seg1-relay",
                            ProbeName = "Segment 1 SMTP-Relay",
                            Status = StateMetricStatus.Critical,
                            ErrorMessage = "Connection refused",
                            ConsecutiveFailures = 3
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("E-Mail-Probes (Sender)");
        html.Should().Contain("Critical");
        html.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersEmailDeliverySection_WhenCheckIsHealthy()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Healthy,
                EmailDelivery = new Dictionary<string, EmailDeliveryCheckState>
                {
                    {
                        "seg1-check", new EmailDeliveryCheckState
                        {
                            CheckId = "seg1-check",
                            CheckName = "Segment 1 Zustellungscheck",
                            Status = StateMetricStatus.Healthy,
                            LastReceivedAt = new DateTime(2024, 1, 15, 10, 25, 0, DateTimeKind.Utc),
                            AgeMinutes = 5.0,
                            ConsecutiveFailures = 0
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("E-Mail-Zustellung (Checker)");
        html.Should().Contain("Segment 1 Zustellungscheck");
        html.Should().Contain("Healthy");
        html.Should().Contain(">5<");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersEmailDeliverySection_WithWarning_WhenMessageIsStale()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Degraded,
                EmailDelivery = new Dictionary<string, EmailDeliveryCheckState>
                {
                    {
                        "seg1-check", new EmailDeliveryCheckState
                        {
                            CheckId = "seg1-check",
                            CheckName = "Segment 1 Zustellungscheck",
                            Status = StateMetricStatus.Warning,
                            AgeMinutes = 25.0,
                            ConsecutiveFailures = 2
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "server-a.api.html"));
        html.Should().Contain("E-Mail-Zustellung (Checker)");
        html.Should().Contain("Warning");
        html.Should().Contain(">25<");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersEmailMonitoringBadges_OnIndexHtml()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Healthy,
                EmailProbes = new Dictionary<string, EmailProbeState>
                {
                    {
                        "seg1-relay", new EmailProbeState
                        {
                            ProbeId = "seg1-relay",
                            ProbeName = "Segment 1",
                            Status = StateMetricStatus.Healthy
                        }
                    }
                },
                EmailDelivery = new Dictionary<string, EmailDeliveryCheckState>
                {
                    {
                        "seg1-check", new EmailDeliveryCheckState
                        {
                            CheckId = "seg1-check",
                            CheckName = "Segment 1 Check",
                            Status = StateMetricStatus.Healthy,
                            AgeMinutes = 10.0
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "index.html"));
        html.Should().Contain("1/1 Probes");
        html.Should().Contain("1/1 Zustellung");
        html.Should().Contain("badge-ok");
    }

    [Fact]
    public async Task GenerateAllAsync_RendersCriticalEmailProbeBadge_OnIndexHtml()
    {
        var generator = new StaticHtmlGenerator(_tempDirectory, _logger.Object);

        var statuses = new List<RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Unhealthy,
                EmailProbes = new Dictionary<string, EmailProbeState>
                {
                    {
                        "seg1-relay", new EmailProbeState
                        {
                            ProbeId = "seg1-relay",
                            ProbeName = "Segment 1",
                            Status = StateMetricStatus.Critical,
                            ErrorMessage = "SMTP timeout"
                        }
                    }
                }
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        var html = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "index.html"));
        html.Should().Contain("0/1 Probes");
        html.Should().Contain("badge-critical");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
