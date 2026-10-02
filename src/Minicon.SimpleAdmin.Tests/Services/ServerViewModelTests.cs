using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ServerViewModelTests
{
    // ── ToServerChecksConfig: null-return boundary ───────────────────────────

    [Fact]
    public void ToServerChecksConfig_AllOff_ReturnsNull()
    {
        // All defaults: services disabled, no checks → should not produce a checks object
        var vm = new ServerChecksViewModel();
        // ServicesEnabled defaults to false, AppPools.Enabled false, no SQL

        vm.ToServerChecksConfig().Should().BeNull();
    }

    [Fact]
    public void ToServerChecksConfig_ServicesEnabledWithService_ReturnsNonNull()
    {
        var vm = new ServerChecksViewModel
        {
            ServicesEnabled = true,
            Services = [new ServiceCheckViewModel { Name = "SQLWriter" }]
        };

        var result = vm.ToServerChecksConfig();

        result.Should().NotBeNull();
        result!.Services!.Enabled.Should().BeTrue();
        result.Services.Checks.Should().ContainSingle(c => c.Name == "SQLWriter");
    }

    [Fact]
    public void ToServerChecksConfig_ServicesDisabledWithService_PreservesServiceList()
    {
        // Disabled toggle but service entry exists — list is preserved in case user re-enables
        var vm = new ServerChecksViewModel
        {
            ServicesEnabled = false,
            Services = [new ServiceCheckViewModel { Name = "SQLWriter" }]
        };

        var result = vm.ToServerChecksConfig();

        result.Should().NotBeNull();
        result!.Services!.Enabled.Should().BeFalse();
        result.Services.Checks.Should().ContainSingle(c => c.Name == "SQLWriter");
    }

    [Fact]
    public void ToServerChecksConfig_AppPoolsEnabled_ReturnsNonNull()
    {
        var vm = new ServerChecksViewModel
        {
            AppPools = new AppPoolChecksViewModel { Enabled = true }
        };

        vm.ToServerChecksConfig().Should().NotBeNull();
    }

    [Fact]
    public void ToServerChecksConfig_SqlConfigured_ReturnsNonNull()
    {
        var vm = new ServerChecksViewModel
        {
            SqlQueryChecks = new SqlQueryChecksViewModel
            {
                Enabled = true,
                Checks = [new SqlQueryCheckViewModel
                {
                    Name = "biztalk-db",
                    ConnectionString = "Server=.;Database=test;",
                    Queries = [new SqlQueryViewModel { Sql = "SELECT 1", Name = "test" }]
                }]
            }
        };

        var result = vm.ToServerChecksConfig();

        result.Should().NotBeNull();
        result!.SqlQueries.Should().NotBeNull();
        result.SqlQueries!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ToServerChecksConfig_EmptyServiceNamesFiltered()
    {
        var vm = new ServerChecksViewModel
        {
            ServicesEnabled = true,
            Services =
            [
                new ServiceCheckViewModel { Name = "SQLWriter" },
                new ServiceCheckViewModel { Name = "" },          // empty — filtered
                new ServiceCheckViewModel { Name = "   " },       // whitespace — filtered
            ]
        };

        var result = vm.ToServerChecksConfig();

        result!.Services!.Checks.Should().ContainSingle(c => c.Name == "SQLWriter");
    }

    // ── ToServerSqlQueriesConfig ─────────────────────────────────────────────

    [Fact]
    public void ToServerSqlQueriesConfig_DisabledWithNoChecks_ReturnsNull()
    {
        var vm = new SqlQueryChecksViewModel { Enabled = false };
        vm.ToServerSqlQueriesConfig().Should().BeNull();
    }

    [Fact]
    public void ToServerSqlQueriesConfig_EnabledWithNoChecks_ReturnsConfigWithEmptyList()
    {
        var vm = new SqlQueryChecksViewModel { Enabled = true };

        var result = vm.ToServerSqlQueriesConfig();

        result.Should().NotBeNull();
        result!.Enabled.Should().BeTrue();
        result.Checks.Should().BeEmpty();
    }

    [Fact]
    public void ToServerSqlQueriesConfig_CheckWithNoName_IsFiltered()
    {
        var vm = new SqlQueryChecksViewModel
        {
            Enabled = true,
            Checks = [new SqlQueryCheckViewModel { Name = "", ConnectionString = "Server=.;" }]
        };

        vm.ToServerSqlQueriesConfig()!.Checks.Should().BeEmpty();
    }

    [Fact]
    public void ToServerSqlQueriesConfig_CheckWithNoConnectionString_IsFiltered()
    {
        var vm = new SqlQueryChecksViewModel
        {
            Enabled = true,
            Checks = [new SqlQueryCheckViewModel { Name = "biztalk-db", ConnectionString = "" }]
        };

        vm.ToServerSqlQueriesConfig()!.Checks.Should().BeEmpty();
    }

    [Fact]
    public void ToServerSqlQueriesConfig_ValidCheck_IncludedInResult()
    {
        var vm = new SqlQueryChecksViewModel
        {
            Enabled = true,
            Checks = [new SqlQueryCheckViewModel
            {
                Name = "biztalk-db",
                ConnectionString = "Server=.;Database=test;",
                Queries = [new SqlQueryViewModel { Sql = "SELECT 1", Name = "q1" }]
            }]
        };

        var result = vm.ToServerSqlQueriesConfig();

        result!.Checks.Should().ContainSingle(c => c.Name == "biztalk-db");
    }

    // ── FromServerChecksConfig ───────────────────────────────────────────────

    [Fact]
    public void FromServerChecksConfig_Null_ReturnsDefaults()
    {
        var vm = ServerChecksViewModel.FromServerChecksConfig(null);

        vm.ServicesEnabled.Should().BeFalse();
        vm.Services.Should().BeEmpty();
        vm.AppPools.Enabled.Should().BeFalse();
        vm.EventLogEnabled.Should().BeFalse();
        vm.SqlQueryChecks.Enabled.Should().BeFalse();
    }

    [Fact]
    public void FromServerChecksConfig_ServicesDisabled_ReturnsFalse()
    {
        var config = new ServerChecksConfig
        {
            Services = new ServerServicesConfig { Enabled = false, Checks = [] }
        };

        ServerChecksViewModel.FromServerChecksConfig(config).ServicesEnabled.Should().BeFalse();
    }

    [Fact]
    public void FromServerChecksConfig_ServicesEnabled_ReturnsTrue()
    {
        var config = new ServerChecksConfig
        {
            Services = new ServerServicesConfig
            {
                Enabled = true,
                Checks = [new ServiceCheckConfig { Name = "SQLWriter" }]
            }
        };

        var vm = ServerChecksViewModel.FromServerChecksConfig(config);

        vm.ServicesEnabled.Should().BeTrue();
        vm.Services.Should().ContainSingle(s => s.Name == "SQLWriter");
    }

    // ── Round-trip: FromServer → ToServer ────────────────────────────────────

    [Fact]
    public void RoundTrip_ServicesAndSqlPreserved()
    {
        var original = new Server
        {
            Name = "TEST-SRV",
            Active = true,
            Checks = new ServerChecksConfig
            {
                Services = new ServerServicesConfig
                {
                    Enabled = true,
                    Checks = [new ServiceCheckConfig { Name = "SQLWriter", MonitorAutomatic = true }]
                },
                SqlQueries = new ServerSqlQueriesConfig
                {
                    Enabled = true,
                    Checks = [new SqlQueryCheckConfig
                    {
                        Name = "biztalk-db",
                        ConnectionString = "Server=.;",
                        Queries = [new SqlQueryConfig { Name = "q1", Sql = "SELECT 1" }]
                    }]
                }
            }
        };

        var result = ServerViewModel.FromServer(original).ToServer();

        result.Checks!.Services!.Enabled.Should().BeTrue();
        result.Checks.Services.Checks.Should().ContainSingle(c => c.Name == "SQLWriter");
        result.Checks.SqlQueries!.Enabled.Should().BeTrue();
        result.Checks.SqlQueries.Checks.Should().ContainSingle(c => c.Name == "biztalk-db");
    }

    [Fact]
    public void RoundTrip_NullChecks_PreservesNull()
    {
        var original = new Server { Name = "TEST-SRV", Active = true, Checks = null };

        var result = ServerViewModel.FromServer(original).ToServer();

        result.Checks.Should().BeNull();
    }

    // ── Email Probe round-trip tests ─────────────────────────────────────────

    [Fact]
    public void ToServerEmailProbesConfig_EnabledWithProbe_ReturnsConfig()
    {
        var vm = new EmailProbesViewModel
        {
            Enabled = true,
            Probes = [new EmailProbeEditorViewModel
            {
                Name = "seg1-relay",
                SmtpHost = "localhost",
                SmtpPort = 25,
                SmtpFrom = "monitor@test.local",
                SmtpTo = "inbox@test.local",
                Subject = "[SA-Probe] seg1-relay",
                SendIntervalMinutes = 15
            }]
        };

        var result = vm.ToServerEmailProbesConfig();

        result.Should().NotBeNull();
        result!.Enabled.Should().BeTrue();
        result.Probes.Should().ContainSingle(p => p.Name == "seg1-relay");
        result.Probes[0].Smtp.Host.Should().Be("localhost");
        result.Probes[0].Smtp.From.Should().Be("monitor@test.local");
        result.Probes[0].Subject.Should().Be("[SA-Probe] seg1-relay");
    }

    [Fact]
    public void ToServerEmailProbesConfig_DisabledWithNoProbes_ReturnsNull()
    {
        var vm = new EmailProbesViewModel { Enabled = false };
        vm.ToServerEmailProbesConfig().Should().BeNull();
    }

    [Fact]
    public void ToServerEmailProbesConfig_EnabledWithUnnamedProbe_FilteredOut()
    {
        var vm = new EmailProbesViewModel
        {
            Enabled = true,
            Probes = [new EmailProbeEditorViewModel { Name = "" }]
        };

        var result = vm.ToServerEmailProbesConfig();

        result.Should().NotBeNull();
        result!.Probes.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_EmailProbeSslModePreserved()
    {
        var original = new EmailProbeConfig
        {
            Name = "seg1-relay",
            Smtp = new SmtpConfig
            {
                Host = "localhost",
                Port = 25,
                SslMode = "auto",
                UseSsl = false,
                From = "monitor@test.local",
                To = "inbox@test.local"
            },
            Subject = "[SA-Probe] seg1-relay"
        };

        var result = EmailProbeEditorViewModel.FromEmailProbeConfig(original, 0).ToEmailProbeConfig();

        result.Smtp.SslMode.Should().Be("auto");
        result.Smtp.UseSsl.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_EmailProbesPreserved()
    {
        var original = new Server
        {
            Name = "TEST-SRV",
            Active = true,
            Checks = new ServerChecksConfig
            {
                EmailProbes = new ServerEmailProbesConfig
                {
                    Enabled = true,
                    Probes = [new EmailProbeConfig
                    {
                        Name = "seg1-relay",
                        Smtp = new SmtpConfig
                        {
                            Host = "localhost",
                            Port = 25,
                            From = "monitor@test.local",
                            To = "inbox@test.local"
                        },
                        Subject = "[SA-Probe] seg1-relay",
                        SendIntervalMinutes = 15
                    }]
                }
            }
        };

        var result = ServerViewModel.FromServer(original).ToServer();

        result.Checks.Should().NotBeNull();
        result.Checks!.EmailProbes.Should().NotBeNull();
        result.Checks.EmailProbes!.Enabled.Should().BeTrue();
        result.Checks.EmailProbes.Probes.Should().ContainSingle(p => p.Name == "seg1-relay");
    }

    [Fact]
    public void ToServerChecksConfig_EmailProbesEnabled_IncludedInResult()
    {
        var vm = new ServerChecksViewModel
        {
            EmailProbes = new EmailProbesViewModel
            {
                Enabled = true,
                Probes = [new EmailProbeEditorViewModel
                {
                    Name = "seg1-relay",
                    SmtpHost = "localhost",
                    SmtpFrom = "monitor@test.local",
                    SmtpTo = "inbox@test.local",
                    Subject = "[SA-Probe] seg1-relay"
                }]
            }
        };

        var result = vm.ToServerChecksConfig();

        result.Should().NotBeNull();
        result!.EmailProbes.Should().NotBeNull();
        result.EmailProbes!.Enabled.Should().BeTrue();
        result.EmailProbes.Probes.Should().ContainSingle(p => p.Name == "seg1-relay");
    }

    // ── Email Delivery round-trip tests ──────────────────────────────────────

    [Fact]
    public void ToServerEmailDeliveryConfig_EnabledWithCheck_ReturnsConfig()
    {
        var vm = new EmailDeliveryViewModel
        {
            Enabled = true,
            ImapHost = "imap.test.local",
            ImapPort = 993,
            ImapUsername = "user",
            ImapPassword = "pass",
            Checks = [new EmailDeliveryCheckEditorViewModel
            {
                Name = "seg1-check",
                SubjectFilter = "[SA-Probe] seg1-relay",
                MaxAgeWarningMinutes = 20,
                MaxAgeCriticalMinutes = 60
            }]
        };

        var result = vm.ToServerEmailDeliveryConfig();

        result.Should().NotBeNull();
        result!.Enabled.Should().BeTrue();
        result.Imap.Host.Should().Be("imap.test.local");
        result.Checks.Should().ContainSingle(c => c.Name == "seg1-check");
        result.Checks[0].SubjectFilter.Should().Be("[SA-Probe] seg1-relay");
    }

    [Fact]
    public void ToServerEmailDeliveryConfig_DisabledNoHostNoChecks_ReturnsNull()
    {
        var vm = new EmailDeliveryViewModel { Enabled = false };
        vm.ToServerEmailDeliveryConfig().Should().BeNull();
    }

    [Fact]
    public void RoundTrip_EmailDeliverySslModePreserved()
    {
        var original = new ServerEmailDeliveryConfig
        {
            Enabled = true,
            Imap = new ImapConfig
            {
                Host = "imap.test.local",
                Port = 993,
                SslMode = "auto",
                UseSsl = false,
                Username = "user",
                Password = "pass"
            },
            Checks = []
        };

        var result = EmailDeliveryViewModel.FromServerEmailDeliveryConfig(original).ToServerEmailDeliveryConfig();

        result.Should().NotBeNull();
        result!.Imap.SslMode.Should().Be("auto");
        result.Imap.UseSsl.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_EmailDeliveryPreserved()
    {
        var original = new Server
        {
            Name = "TEST-SRV",
            Active = true,
            Checks = new ServerChecksConfig
            {
                EmailDelivery = new ServerEmailDeliveryConfig
                {
                    Enabled = true,
                    Imap = new ImapConfig
                    {
                        Host = "imap.test.local",
                        Port = 993,
                        Username = "user",
                        Password = "pass",
                        Folder = "INBOX"
                    },
                    Checks = [new EmailDeliveryCheckConfig
                    {
                        Name = "seg1-check",
                        SubjectFilter = "[SA-Probe] seg1-relay",
                        MaxAgeWarningMinutes = 20,
                        MaxAgeCriticalMinutes = 60,
                        DeleteAfterCheck = true
                    }]
                }
            }
        };

        var result = ServerViewModel.FromServer(original).ToServer();

        result.Checks.Should().NotBeNull();
        result.Checks!.EmailDelivery.Should().NotBeNull();
        result.Checks.EmailDelivery!.Enabled.Should().BeTrue();
        result.Checks.EmailDelivery.Imap.Host.Should().Be("imap.test.local");
        result.Checks.EmailDelivery.Checks.Should().ContainSingle(c => c.Name == "seg1-check");
    }
}
