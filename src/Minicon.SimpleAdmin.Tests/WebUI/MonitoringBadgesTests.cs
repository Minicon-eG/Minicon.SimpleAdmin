using System.Text.Json;
using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using MetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;
using RuntimeStatus = Minicon.SimpleAdmin.Models.Status.RuntimeStatus;
using Minicon.SimpleAdmin.WebUI.Helpers;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public class MonitoringBadgesTests
{
    private static readonly string[] AllKeys =
    [
        "apppools", "services", "eventlog", "sql", "emailprobes", "emaildelivery",
        "certificates", "biztalk", "files", "scheduledtasks"
    ];

    [Fact]
    public void AllActiveFeatures_WithoutRuntimeData_ReturnNeutralBadges()
    {
        var badges = MonitoringBadges.Create(EnabledConfig(), EmptyStatus());

        badges.Select(b => b.Key).Should().Equal(AllKeys);
        badges.Should().OnlyContain(b => b.Tone == "muted" && !b.HasData);
        badges.Should().OnlyContain(b => b.Title.Contains("noch keine Daten", StringComparison.Ordinal));
        badges.Should().OnlyContain(b => b.Icon.StartsWith("bi-", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonConfig_WithExplicitNullBlocks_DoesNotThrowOrEnableBadges()
    {
        const string json = """
            {
              "features": {
                "appPools": null,
                "services": null,
                "eventLog": null,
                "sqlQueries": null,
                "certificates": null,
                "bizTalk": null,
                "fileMonitoring": null,
                "scheduledTasks": null
              },
              "servers": [{
                "name": "srv01",
                "active": true,
                "checks": {
                  "appPools": null,
                  "services": null,
                  "eventLog": null,
                  "sqlQueries": null,
                  "certificates": null,
                  "bizTalk": null,
                  "fileMonitoring": null,
                  "scheduledTasks": null,
                  "emailProbes": { "enabled": true, "probes": null },
                  "emailDelivery": { "enabled": true, "checks": null }
                }
              }]
            }
            """;
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var act = () => MonitoringBadges.Create(config, EmptyStatus());

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Theory]
    [InlineData("apppools")]
    [InlineData("services")]
    [InlineData("eventlog")]
    [InlineData("sql")]
    [InlineData("certificates")]
    [InlineData("biztalk")]
    [InlineData("files")]
    [InlineData("scheduledtasks")]
    public void GlobalFeatureDisabled_RemovesBadge(string key)
    {
        var config = EnabledConfig();
        SetGlobalEnabled(config, key, false);

        var badges = MonitoringBadges.Create(config, EmptyStatus());

        badges.Select(b => b.Key).Should().NotContain(key);
        badges.Should().HaveCount(AllKeys.Length - 1);
    }

    [Theory]
    [MemberData(nameof(AllFeatureKeys))]
    public void ServerFeatureDisabled_RemovesBadge(string key)
    {
        var config = EnabledConfig();
        SetServerEnabled(config, key, false);

        var badges = MonitoringBadges.Create(config, EmptyStatus());

        badges.Select(b => b.Key).Should().NotContain(key);
        badges.Should().HaveCount(AllKeys.Length - 1);
    }

    [Fact]
    public void InactiveOrUnknownServer_ReturnsNoBadges()
    {
        var inactive = EnabledConfig();
        inactive.Servers[0].Active = false;
        MonitoringBadges.Create(inactive, EmptyStatus()).Should().BeEmpty();

        MonitoringBadges.Create(EnabledConfig(), new ServerStatusView { ServerId = "other" }).Should().BeEmpty();
    }

    [Fact]
    public void EmptyConfiguredChecks_UseTheSameGatesAsWorker()
    {
        var config = EnabledConfig();
        var checks = config.Servers[0].Checks!;
        checks.Services!.Checks.Clear();
        checks.SqlQueries!.Checks.Clear();
        checks.EmailProbes!.Probes.Clear();
        checks.EmailDelivery!.Checks.Clear();
        checks.Certificates!.Checks.Clear();
        checks.FileMonitoring!.Directories.Clear();
        checks.FileMonitoring.LogScans.Clear();
        checks.ScheduledTasks!.Tasks.Clear();

        var badges = MonitoringBadges.Create(config, EmptyStatus());

        badges.Select(b => b.Key).Should().Equal("apppools", "eventlog", "biztalk");
    }

    [Fact]
    public void AppPools_CountHealthyPoolsAndMapWarningAndCritical()
    {
        var status = EmptyStatus();
        status.AppPools = new()
        {
            ["ok"] = new() { Status = AppPoolStatus.Running },
            ["finding"] = new() { Status = AppPoolStatus.Running, MemoryStatus = MetricStatus.Warning }
        };

        var badge = Badge(status, "apppools");
        badge.Text.Should().Be("1/2 Pools");
        badge.Tone.Should().Be("warn");

        status.AppPools["finding"].MemoryStatus = MetricStatus.Critical;
        Badge(status, "apppools").Tone.Should().Be("crit");

        status.AppPools["finding"].MemoryStatus = MetricStatus.Healthy;
        status.AppPools["finding"].Status = AppPoolStatus.Stopped;
        Badge(status, "apppools").Tone.Should().Be("crit");
    }

    [Fact]
    public void WindowsServices_CountRunningServices()
    {
        var status = EmptyStatus();
        status.WindowsServices = new()
        {
            ["ok"] = new() { Status = "Running" },
            ["stopped"] = new() { Status = "Stopped" }
        };

        var badge = Badge(status, "services");
        badge.Text.Should().Be("1/2 Dienste");
        badge.Tone.Should().Be("crit");
    }

    [Theory]
    [InlineData(MetricStatus.Healthy, "ok")]
    [InlineData(MetricStatus.Warning, "warn")]
    [InlineData(MetricStatus.Critical, "crit")]
    public void EventLog_MapsStatusAndCount(MetricStatus metricStatus, string tone)
    {
        var status = EmptyStatus();
        status.EventLogs = new() { EventCount = 3, IgnoredCount = 2, Status = metricStatus };

        var badge = Badge(status, "eventlog");
        badge.Text.Should().Be("3 Events");
        badge.Tone.Should().Be(tone);
        badge.Title.Should().Contain("2 ignoriert");
    }

    [Fact]
    public void SqlQueries_CountOnlyHealthyChecksAsOk()
    {
        var status = EmptyStatus();
        status.SqlQueryChecks = new()
        {
            ["ok"] = new() { Status = MetricStatus.Healthy },
            ["warning"] = new() { Status = MetricStatus.Warning }
        };

        var badge = Badge(status, "sql");
        badge.Text.Should().Be("1/2 SQL");
        badge.Tone.Should().Be("warn");

        status.SqlQueryChecks["warning"].Status = MetricStatus.Critical;
        Badge(status, "sql").Tone.Should().Be("crit");
    }

    [Fact]
    public void SqlQueries_EmptyRuntimeDictionaryIsDataNotMissingData()
    {
        var status = EmptyStatus();
        status.Services[0].SqlQueryChecks = new();

        var badge = Badge(status, "sql");
        badge.HasData.Should().BeTrue();
        badge.Text.Should().Be("0/0 SQL");
        badge.Tone.Should().Be("ok");
        badge.Title.Should().NotContain("noch keine Daten");
    }

    [Fact]
    public void EmailProbes_MapWarningAndCritical()
    {
        var status = EmptyStatus();
        status.EmailProbes = new()
        {
            ["ok"] = new() { Status = MetricStatus.Healthy },
            ["finding"] = new() { Status = MetricStatus.Warning }
        };

        Badge(status, "emailprobes").Tone.Should().Be("warn");
        Badge(status, "emailprobes").Text.Should().Be("1/2 Probes");

        status.EmailProbes["finding"].Status = MetricStatus.Critical;
        Badge(status, "emailprobes").Tone.Should().Be("crit");
    }

    [Fact]
    public void EmailDelivery_MapsWarningAndCritical()
    {
        var status = EmptyStatus();
        status.EmailDelivery = new()
        {
            ["ok"] = new() { Status = MetricStatus.Healthy },
            ["finding"] = new() { Status = MetricStatus.Warning }
        };

        Badge(status, "emaildelivery").Tone.Should().Be("warn");
        Badge(status, "emaildelivery").Text.Should().Be("1/2 Zustellung");

        status.EmailDelivery["finding"].Status = MetricStatus.Critical;
        Badge(status, "emaildelivery").Tone.Should().Be("crit");
    }

    [Fact]
    public void Certificates_MapWarningAndCritical()
    {
        var status = EmptyStatus();
        status.Certificates = new()
        {
            ["ok"] = new() { Status = MetricStatus.Healthy },
            ["finding"] = new() { Status = MetricStatus.Warning }
        };

        Badge(status, "certificates").Tone.Should().Be("warn");
        Badge(status, "certificates").Text.Should().Be("1/2 Zertifikate");

        status.Certificates["finding"].Status = MetricStatus.Critical;
        Badge(status, "certificates").Tone.Should().Be("crit");
    }

    [Fact]
    public void UnknownMetricStatuses_AreNeutralAndNotCountedAsHealthy()
    {
        var status = EmptyStatus();
        status.EventLogs = new() { Status = MetricStatus.Unknown };
        Badge(status, "eventlog").Tone.Should().Be("muted");

        status.Certificates = new()
        {
            ["ok"] = new() { Status = MetricStatus.Healthy },
            ["unknown"] = new() { Status = MetricStatus.Unknown }
        };
        var badge = Badge(status, "certificates");
        badge.Text.Should().Be("1/2 Zertifikate");
        badge.Tone.Should().Be("muted");
    }

    [Fact]
    public void BizTalk_CountsArtifactsAndMapsApiFailure()
    {
        var status = EmptyStatus();
        status.BizTalk = new()
        {
            ApplicationsTotal = 4,
            Problems = [new BizTalkArtifact { IsCritical = false }]
        };

        var badge = Badge(status, "biztalk");
        badge.Text.Should().Be("3/4 BizTalk");
        badge.Tone.Should().Be("warn");

        status.BizTalk.SuspendedStatus = MetricStatus.Critical;
        Badge(status, "biztalk").Tone.Should().Be("crit");

        status.BizTalk.ApiReachable = false;
        badge = Badge(status, "biztalk");
        badge.Text.Should().Be("BizTalk API");
        badge.Tone.Should().Be("crit");
    }

    [Fact]
    public void FileMonitoring_CountsFindingsAndMapsErrorAndBusinessHours()
    {
        var status = EmptyStatus();
        status.FileMonitoring = new() { StuckFiles = [new StuckFile { IsCritical = false }] };

        var badge = Badge(status, "files");
        badge.Text.Should().Be("1 Funde");
        badge.Tone.Should().Be("warn");

        status.FileMonitoring.AnyError = true;
        Badge(status, "files").Tone.Should().Be("crit");

        status.FileMonitoring.OutsideBusinessHours = true;
        badge = Badge(status, "files");
        badge.Tone.Should().Be("muted");
        badge.Text.Should().Be("Dateien pausiert");
    }

    [Fact]
    public void ScheduledTasks_CountFindingsAndLinkToExistingAnchor()
    {
        var status = EmptyStatus();
        status.ScheduledTasks = new()
        {
            Tasks =
            [
                new ScheduledTaskInfo(),
                new ScheduledTaskInfo { LastRunFailed = true, FailureIsCritical = false }
            ]
        };

        var badge = Badge(status, "scheduledtasks");
        badge.Text.Should().Be("1/2 Tasks");
        badge.Tone.Should().Be("warn");
        badge.Fragment.Should().Be("#scheduled-tasks");

        status.ScheduledTasks.Tasks[1].FailureIsCritical = true;
        Badge(status, "scheduledtasks").Tone.Should().Be("crit");
    }

    [Fact]
    public void ScheduledTasks_QueryFailureIsWarningAndKeepsAnchor()
    {
        var status = EmptyStatus();
        status.ScheduledTasks = new() { QueryFailed = true };

        var badge = Badge(status, "scheduledtasks");
        badge.Text.Should().Be("Tasks – Abfragefehler");
        badge.Tone.Should().Be("warn");
        badge.Fragment.Should().Be("#scheduled-tasks");
    }

    [Fact]
    public void AllBadges_HaveStableDetailsAnchors_IncludingRawScheduledTasks()
    {
        var expected = new Dictionary<string, string>
        {
            ["apppools"] = "#app-pools",
            ["services"] = "#windows-services",
            ["eventlog"] = "#event-log",
            ["sql"] = "#sql-queries",
            ["emailprobes"] = "#email-probes",
            ["emaildelivery"] = "#email-delivery",
            ["certificates"] = "#certificates",
            ["biztalk"] = "#biztalk",
            ["files"] = "#file-monitoring",
            ["scheduledtasks"] = "#scheduled-tasks"
        };

        var badges = MonitoringBadges.Create(EnabledConfig(), EmptyStatus());

        badges.ToDictionary(b => b.Key, b => b.Fragment).Should().BeEquivalentTo(expected);
        var rawStatus = EmptyStatus();
        rawStatus.Services[0].ScheduledTasks = new();
        Badge(rawStatus, "scheduledtasks").Fragment.Should().Be("#scheduled-tasks");
    }

    public static TheoryData<string> AllFeatureKeys() => new(AllKeys);

    private static MonitoringBadge Badge(ServerStatusView status, string key) =>
        MonitoringBadges.Create(EnabledConfig(), status).Single(b => b.Key == key);

    private static ServerStatusView EmptyStatus() => new()
    {
        ServerId = "srv01",
        Services = [new RuntimeStatus { Server = "srv01", Service = "prtg" }]
    };

    private static Config EnabledConfig() => new()
    {
        Features = new()
        {
            AppPools = new() { Enabled = true },
            Services = new() { Enabled = true },
            EventLog = new() { Enabled = true },
            SqlQueries = new() { Enabled = true },
            Certificates = new() { Enabled = true },
            BizTalk = new() { Enabled = true },
            FileMonitoring = new() { Enabled = true },
            ScheduledTasks = new() { Enabled = true }
        },
        Servers =
        [
            new Server
            {
                Name = "srv01",
                Active = true,
                Checks = new()
                {
                    AppPools = new() { Enabled = true, DiscoverAll = true },
                    Services = new() { Enabled = true, Checks = [new ServiceCheckConfig { Name = "Spooler" }] },
                    EventLog = new() { Enabled = true },
                    SqlQueries = new() { Enabled = true, Checks = [new SqlQueryCheckConfig { Name = "sql" }] },
                    EmailProbes = new() { Enabled = true, Probes = [new EmailProbeConfig { Name = "probe" }] },
                    EmailDelivery = new()
                    {
                        Enabled = true,
                        Checks = [new EmailDeliveryCheckConfig { Name = "delivery" }]
                    },
                    Certificates = new()
                    {
                        Enabled = true,
                        Checks = [new CertificateCheckConfig { Source = "store://LocalMachine/My" }]
                    },
                    BizTalk = new() { Enabled = true },
                    FileMonitoring = new()
                    {
                        Enabled = true,
                        Directories = [new WatchedDirectory { Name = "files", Path = "/tmp" }]
                    },
                    ScheduledTasks = new()
                    {
                        Enabled = true,
                        Tasks = [new ScheduledTaskWatchConfig { Pattern = "\\Minicon\\*" }]
                    }
                }
            }
        ]
    };

    private static void SetGlobalEnabled(Config config, string key, bool enabled)
    {
        var features = config.Features!;
        switch (key)
        {
            case "apppools": features.AppPools.Enabled = enabled; break;
            case "services": features.Services.Enabled = enabled; break;
            case "eventlog": features.EventLog.Enabled = enabled; break;
            case "sql": features.SqlQueries.Enabled = enabled; break;
            case "certificates": features.Certificates.Enabled = enabled; break;
            case "biztalk": features.BizTalk.Enabled = enabled; break;
            case "files": features.FileMonitoring.Enabled = enabled; break;
            case "scheduledtasks": features.ScheduledTasks.Enabled = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    private static void SetServerEnabled(Config config, string key, bool enabled)
    {
        var checks = config.Servers[0].Checks!;
        switch (key)
        {
            case "apppools": checks.AppPools!.Enabled = enabled; break;
            case "services": checks.Services!.Enabled = enabled; break;
            case "eventlog": checks.EventLog!.Enabled = enabled; break;
            case "sql": checks.SqlQueries!.Enabled = enabled; break;
            case "emailprobes": checks.EmailProbes!.Enabled = enabled; break;
            case "emaildelivery": checks.EmailDelivery!.Enabled = enabled; break;
            case "certificates": checks.Certificates!.Enabled = enabled; break;
            case "biztalk": checks.BizTalk!.Enabled = enabled; break;
            case "files": checks.FileMonitoring!.Enabled = enabled; break;
            case "scheduledtasks": checks.ScheduledTasks!.Enabled = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }
}
