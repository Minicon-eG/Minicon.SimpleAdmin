using System.Text.Json;
using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ConfigurationServiceTests
{
    private static ConfigurationService CreateService(string masterKey = "")
    {
        var dict = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(masterKey))
            dict["Encryption:ConnectionStringKey"] = masterKey;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var service = new ConfigurationService(config);
        service.CreateNew();
        return service;
    }

    private static Server MakeServer(string name) =>
        new() { Name = name, Active = true };

    // ── AddServer ────────────────────────────────────────────────────────────

    [Fact]
    public void AddServer_DuplicateName_ThrowsInvalidOperation()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));

        var act = () => svc.AddServer(MakeServer("SRV1"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*SRV1*");
    }

    [Fact]
    public void AddServer_DuplicateNameCaseInsensitive_Throws()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));

        var act = () => svc.AddServer(MakeServer("srv1"));
        act.Should().Throw<InvalidOperationException>();
    }

    // ── GetServer ────────────────────────────────────────────────────────────

    [Fact]
    public void GetServer_ExactName_ReturnsServer()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));

        svc.GetServer("SRV1").Should().NotBeNull();
    }

    [Fact]
    public void GetServer_DifferentCase_ReturnsServer()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));

        svc.GetServer("srv1").Should().NotBeNull();
    }

    [Fact]
    public void GetServer_NotFound_ReturnsNull()
    {
        var svc = CreateService();

        svc.GetServer("NONEXISTENT").Should().BeNull();
    }

    // ── UpdateServer ─────────────────────────────────────────────────────────

    [Fact]
    public void UpdateServer_RenameToExistingName_Throws()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));
        svc.AddServer(MakeServer("SRV2"));

        var act = () => svc.UpdateServer("SRV1", MakeServer("SRV2"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*SRV2*");
    }

    [Fact]
    public void UpdateServer_Rename_UpdatesEnvironmentReferences()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("OLD-NAME"));
        svc.SetEnvironmentServers("development", "services", ["OLD-NAME", "OTHER-SERVER"]);
        svc.SetEnvironmentServers("production", "transfer", ["OLD-NAME"]);

        svc.UpdateServer("OLD-NAME", MakeServer("NEW-NAME"));

        svc.GetEnvironmentServers("development", "services")
           .Should().Contain("NEW-NAME").And.NotContain("OLD-NAME");
        svc.GetEnvironmentServers("production", "transfer")
           .Should().Contain("NEW-NAME").And.NotContain("OLD-NAME");
    }

    [Fact]
    public void UpdateServer_SameName_Succeeds()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));

        var act = () => svc.UpdateServer("SRV1", new Server { Name = "SRV1", Active = false });
        act.Should().NotThrow();
        svc.GetServer("SRV1")!.Active.Should().BeFalse();
    }

    // ── DeleteServer ─────────────────────────────────────────────────────────

    [Fact]
    public void DeleteServer_RemovesFromAllEnvironmentCategories()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));
        svc.SetEnvironmentServers("production", "transfer",  ["SRV1", "SRV2"]);
        svc.SetEnvironmentServers("production", "services",  ["SRV1"]);
        svc.SetEnvironmentServers("production", "biztalk",   ["SRV1"]);
        svc.SetEnvironmentServers("production", "database",  ["SRV1"]);

        svc.DeleteServer("SRV1");

        svc.GetEnvironmentServers("production", "transfer") .Should().NotContain("SRV1");
        svc.GetEnvironmentServers("production", "services") .Should().NotContain("SRV1");
        svc.GetEnvironmentServers("production", "biztalk")  .Should().NotContain("SRV1");
        svc.GetEnvironmentServers("production", "database") .Should().NotContain("SRV1");
    }

    [Fact]
    public void DeleteServer_OtherServersInEnvironment_Unchanged()
    {
        var svc = CreateService();
        svc.AddServer(MakeServer("SRV1"));
        svc.AddServer(MakeServer("SRV2"));
        svc.SetEnvironmentServers("production", "transfer", ["SRV1", "SRV2"]);

        svc.DeleteServer("SRV1");

        svc.GetEnvironmentServers("production", "transfer").Should().ContainSingle("SRV2");
    }

    [Fact]
    public void DeleteServer_NonExistent_DoesNotThrow()
    {
        var svc = CreateService();

        var act = () => svc.DeleteServer("NONEXISTENT");
        act.Should().NotThrow();
    }

    // ── EncryptServerConnectionStrings ───────────────────────────────────────

    [Fact]
    public void EncryptServerConnectionStrings_NoMasterKey_DoesNotEncrypt()
    {
        var svc = CreateService(masterKey: ""); // no key
        var server = new Server
        {
            Name = "SRV1",
            Checks = new ServerChecksConfig
            {
                SqlQueries = new ServerSqlQueriesConfig
                {
                    Enabled = true,
                    Checks = [new SqlQueryCheckConfig { Name = "db", ConnectionString = "Server=.;Database=test;" }]
                }
            }
        };

        svc.EncryptServerConnectionStrings(server);

        server.Checks.SqlQueries.Checks[0].ConnectionString.Should().Be("Server=.;Database=test;");
    }

    [Fact]
    public void EncryptServerConnectionStrings_WithMasterKey_EncryptsPlaintext()
    {
        // Valid 32-byte Base64 key
        const string key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var svc = CreateService(masterKey: key);
        var server = new Server
        {
            Name = "SRV1",
            Checks = new ServerChecksConfig
            {
                SqlQueries = new ServerSqlQueriesConfig
                {
                    Enabled = true,
                    Checks = [new SqlQueryCheckConfig { Name = "db", ConnectionString = "Server=.;Database=test;" }]
                }
            }
        };

        svc.EncryptServerConnectionStrings(server);

        server.Checks.SqlQueries.Checks[0].ConnectionString.Should().StartWith("enc:v1:");
    }

    [Fact]
    public void EncryptServerConnectionStrings_AlreadyEncrypted_NotDoubleEncrypted()
    {
        const string key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var svc = CreateService(masterKey: key);

        // Pre-encrypt
        var server = new Server
        {
            Name = "SRV1",
            Checks = new ServerChecksConfig
            {
                SqlQueries = new ServerSqlQueriesConfig
                {
                    Enabled = true,
                    Checks = [new SqlQueryCheckConfig { Name = "db", ConnectionString = "Server=.;" }]
                }
            }
        };
        svc.EncryptServerConnectionStrings(server);
        var firstEncrypted = server.Checks.SqlQueries.Checks[0].ConnectionString;

        // Encrypt again — should be a no-op
        svc.EncryptServerConnectionStrings(server);
        var secondEncrypted = server.Checks.SqlQueries.Checks[0].ConnectionString;

        secondEncrypted.Should().Be(firstEncrypted);
    }

    // ── Round-Trip Tests (Load → Modify → Save → Load) ─────────────────────

    [Fact]
    public async Task RoundTrip_BasicServer_PreservesAllData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            // Arrange: Create config with server
            var svc = CreateService();
            var server = new Server { Name = "TEST-SRV", Active = true, BaseUrl = "https://example.com" };
            svc.AddServer(server);
            svc.SetEnvironmentServers("development", "transfer", ["TEST-SRV"]);

            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            // Act 1: Load from file
            var svc2 = CreateService();
            svc2.LoadFromFile(json1, tempFile);

            // Verify original data loaded
            svc2.GetServer("TEST-SRV").Should().NotBeNull();
            svc2.GetServer("TEST-SRV")!.BaseUrl.Should().Be("https://example.com");
            svc2.GetEnvironmentServers("development", "transfer").Should().Contain("TEST-SRV");

            // Act 2: Modify and save
            svc2.GetServer("TEST-SRV")!.BaseUrl = "https://updated.com";
            await svc2.SaveToFileAsync();

            // Act 3: Load again from saved file
            var svc3 = CreateService();
            var json2 = await File.ReadAllTextAsync(tempFile);
            svc3.LoadFromFile(json2, tempFile);

            // Assert: Changes persisted
            var restored = svc3.GetServer("TEST-SRV");
            restored.Should().NotBeNull();
            restored!.Name.Should().Be("TEST-SRV");
            restored.Active.Should().BeTrue();
            restored.BaseUrl.Should().Be("https://updated.com");
            svc3.GetEnvironmentServers("development", "transfer").Should().Contain("TEST-SRV");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RoundTrip_MultipleServers_PreservesAll()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            var svc = CreateService();
            svc.AddServer(MakeServer("SRV1"));
            svc.AddServer(MakeServer("SRV2"));
            svc.AddServer(MakeServer("SRV3"));
            svc.SetEnvironmentServers("production", "transfer", ["SRV1", "SRV2"]);
            svc.SetEnvironmentServers("development", "services", ["SRV3"]);

            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            var svc2 = CreateService();
            svc2.LoadFromFile(json1, tempFile);
            await svc2.SaveToFileAsync();

            var svc3 = CreateService();
            var json2 = await File.ReadAllTextAsync(tempFile);
            svc3.LoadFromFile(json2, tempFile);

            // Assert
            svc3.GetServers().Should().HaveCount(3);
            svc3.GetServer("SRV1").Should().NotBeNull();
            svc3.GetServer("SRV2").Should().NotBeNull();
            svc3.GetServer("SRV3").Should().NotBeNull();
            svc3.GetEnvironmentServers("production", "transfer").Should().Equal("SRV1", "SRV2");
            svc3.GetEnvironmentServers("development", "services").Should().ContainSingle("SRV3");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RoundTrip_HasChangesFlag_UpdatedCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            var svc = CreateService();
            svc.AddServer(MakeServer("SRV1"));
            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            // Act
            var svc2 = CreateService();
            svc2.LoadFromFile(json1, tempFile);
            svc2.HasChanges.Should().BeFalse(); // Just loaded, no changes

            svc2.AddServer(MakeServer("SRV2"));
            svc2.HasChanges.Should().BeTrue(); // Change made

            await svc2.SaveToFileAsync();
            svc2.HasChanges.Should().BeFalse(); // Changes saved
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Round-Trip Tests with Monitoring Features ────────────────────

    [Fact]
    public async Task RoundTrip_SqlQueryChecks_PreservesAllData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            // Arrange: Create config with SQL Query Checks
            var svc = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var server = new Server
            {
                Name = "DB-SRV",
                Active = true,
                BaseUrl = "https://example.com",
                Checks = new ServerChecksConfig
                {
                    SqlQueries = new ServerSqlQueriesConfig
                    {
                        Enabled = true,
                        Checks = new List<SqlQueryCheckConfig>
                        {
                            new()
                            {
                                Name = "app-db",
                                Description = "Application Database",
                                ConnectionString = "Server=localhost;Database=AppDb;Integrated Security=true;",
                                TimeoutSeconds = 30,
                                Queries = new List<SqlQueryConfig>
                                {
                                    new()
                                    {
                                        Name = "Check Jobs",
                                        Sql = "SELECT COUNT(*) as Cnt FROM Jobs",
                                        RowCount = new SqlRowCountAssertion { ExpectedCount = 0, Operator = "==" },
                                        ColumnAssertions = new List<SqlColumnAssertion>
                                        {
                                            new()
                                            {
                                                Name = "JobCount",
                                                Column = "Cnt",
                                                ExpectedValue = "0",
                                                Operator = "==",
                                                ValueType = SqlValueType.Integer
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
            svc.AddServer(server);
            svc.SetEnvironmentServers("production", "transfer", ["DB-SRV"]);

            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            // Act 1: Load from file
            var svc2 = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            svc2.LoadFromFile(json1, tempFile);

            // Verify loaded data
            var loadedServer = svc2.GetServer("DB-SRV");
            loadedServer.Should().NotBeNull();
            loadedServer!.Checks?.SqlQueries?.Enabled.Should().BeTrue();
            loadedServer.Checks.SqlQueries.Checks.Should().HaveCount(1);
            var check = loadedServer.Checks.SqlQueries.Checks[0];
            check.Name.Should().Be("app-db");
            check.Description.Should().Be("Application Database");
            check.Queries.Should().HaveCount(1);
            check.Queries[0].Name.Should().Be("Check Jobs");
            check.Queries[0].ColumnAssertions.Should().HaveCount(1);

            // Act 2: Modify and save
            var query = loadedServer.Checks.SqlQueries.Checks[0].Queries[0];
            query.Sql = "SELECT COUNT(*) as Cnt FROM UpdatedJobs";
            await svc2.SaveToFileAsync();

            // Act 3: Load again from saved file
            var svc3 = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var json2 = await File.ReadAllTextAsync(tempFile);
            svc3.LoadFromFile(json2, tempFile);

            // Assert: Changes persisted
            var restored = svc3.GetServer("DB-SRV");
            restored.Should().NotBeNull();
            restored!.Checks?.SqlQueries?.Checks[0].Queries[0].Sql.Should().Be("SELECT COUNT(*) as Cnt FROM UpdatedJobs");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RoundTrip_WindowsServices_PreservesAllData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            // Arrange: Create config with Windows Services monitoring
            var svc = CreateService();
            var server = new Server
            {
                Name = "SRV-WITH-SERVICES",
                Active = true,
                Checks = new ServerChecksConfig
                {
                    Services = new ServerServicesConfig
                    {
                        Enabled = true,
                        Checks = new List<ServiceCheckConfig>
                        {
                            new()
                            {
                                Name = "w3svc",
                                DisplayName = "World Wide Web Publishing Service",
                                MonitorAutomatic = true
                            },
                            new()
                            {
                                Name = "MSSQLSERVER",
                                DisplayName = "SQL Server (MSSQLSERVER)",
                                MonitorAutomatic = true
                            }
                        }
                    }
                }
            };
            svc.AddServer(server);

            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            // Act 1: Load from file
            var svc2 = CreateService();
            svc2.LoadFromFile(json1, tempFile);

            // Verify loaded data
            var loadedServer = svc2.GetServer("SRV-WITH-SERVICES");
            loadedServer.Should().NotBeNull();
            loadedServer!.Checks?.Services?.Enabled.Should().BeTrue();
            loadedServer.Checks.Services.Checks.Should().HaveCount(2);
            loadedServer.Checks.Services.Checks[0].Name.Should().Be("w3svc");
            loadedServer.Checks.Services.Checks[1].Name.Should().Be("MSSQLSERVER");

            // Act 2: Add another service and save
            loadedServer.Checks.Services.Checks.Add(new ServiceCheckConfig
            {
                Name = "spooler",
                DisplayName = "Print Spooler",
                MonitorAutomatic = true
            });
            await svc2.SaveToFileAsync();

            // Act 3: Load again
            var svc3 = CreateService();
            var json2 = await File.ReadAllTextAsync(tempFile);
            svc3.LoadFromFile(json2, tempFile);

            // Assert: All services preserved including new one
            var restored = svc3.GetServer("SRV-WITH-SERVICES");
            restored!.Checks.Services.Checks.Should().HaveCount(3);
            restored.Checks.Services.Checks[2].Name.Should().Be("spooler");
            restored.Checks.Services.Checks[2].MonitorAutomatic.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RoundTrip_AllFeaturesEnabled_CompletePreservation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        try
        {
            // Arrange: Create config with multiple features enabled
            var svc = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var server = new Server
            {
                Name = "FULL-FEATURED",
                Active = true,
                BaseUrl = "https://status.example.com",
                Checks = new ServerChecksConfig
                {
                    Services = new ServerServicesConfig
                    {
                        Enabled = true,
                        Checks = new List<ServiceCheckConfig>
                        {
                            new() { Name = "service1", DisplayName = "Service 1", MonitorAutomatic = true }
                        }
                    },
                    SqlQueries = new ServerSqlQueriesConfig
                    {
                        Enabled = true,
                        Checks = new List<SqlQueryCheckConfig>
                        {
                            new()
                            {
                                Name = "check1",
                                Description = "Check 1",
                                ConnectionString = "Server=localhost;",
                                Queries = new List<SqlQueryConfig>
                                {
                                    new() { Name = "q1", Sql = "SELECT 1" }
                                }
                            }
                        }
                    }
                }
            };
            svc.AddServer(server);
            svc.SetEnvironmentServers("production", "transfer", ["FULL-FEATURED"]);
            svc.SetEnvironmentServers("production", "services", ["FULL-FEATURED"]);

            var json1 = svc.ExportToJson();
            await File.WriteAllTextAsync(tempFile, json1);

            // Act: Load and save
            var svc2 = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            svc2.LoadFromFile(json1, tempFile);
            await svc2.SaveToFileAsync();

            var svc3 = CreateService(masterKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            var json2 = await File.ReadAllTextAsync(tempFile);
            svc3.LoadFromFile(json2, tempFile);

            // Assert: All data preserved
            var restored = svc3.GetServer("FULL-FEATURED");
            restored.Should().NotBeNull();
            restored!.BaseUrl.Should().Be("https://status.example.com");
            restored.Checks.Services.Enabled.Should().BeTrue();
            restored.Checks.Services.Checks.Should().HaveCount(1);
            restored.Checks.SqlQueries.Enabled.Should().BeTrue();
            restored.Checks.SqlQueries.Checks.Should().HaveCount(1);

            svc3.GetEnvironmentServers("production", "transfer").Should().Contain("FULL-FEATURED");
            svc3.GetEnvironmentServers("production", "services").Should().Contain("FULL-FEATURED");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Application title ────────────────────────────────────────────────────

    [Fact]
    public void GetApplicationTitle_NotConfigured_ReturnsDefault()
    {
        CreateService().GetApplicationTitle().Should().Be(ConfigurationService.DefaultApplicationTitle);
    }

    [Fact]
    public void UpdateApplicationTitle_TrimsAndStores()
    {
        var svc = CreateService();

        svc.UpdateApplicationTitle("  Monitoring Justiz  ");

        svc.GetApplicationTitle().Should().Be("Monitoring Justiz");
        svc.Config.General!.Title.Should().Be("Monitoring Justiz");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateApplicationTitle_Empty_FallsBackToDefault(string? title)
    {
        var svc = CreateService();
        svc.UpdateApplicationTitle("Alt");

        svc.UpdateApplicationTitle(title);

        svc.Config.General!.Title.Should().BeNull();
        svc.GetApplicationTitle().Should().Be(ConfigurationService.DefaultApplicationTitle);
    }

    [Fact]
    public void UpdateApplicationTitle_TooLong_IsCut()
    {
        var svc = CreateService();

        svc.UpdateApplicationTitle(new string('x', 100));

        svc.GetApplicationTitle().Should().HaveLength(ConfigurationService.MaxApplicationTitleLength);
    }
}

public sealed class ConfigurationServiceDataProtectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa-config-f4-" + Guid.NewGuid().ToString("N"));

    public ConfigurationServiceDataProtectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* ignore */ }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    private ConfigurationService CreateService(ILogger<ConfigurationService>? logger = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new ConfigurationService(configuration, logger);
    }

    [Fact]
    public async Task LoadAndSave_PreservesDomainsAndUnknownFieldsOnEveryModelLevel()
    {
        const string json = """
            {
              "general": { "title": "Alt", "futureGeneral": { "enabled": true } },
              "features": {
                "futureFeature": [1, 2, 3],
                "notifications": { "futureNotification": { "mode": "new" } }
              },
              "output": {
                "futureOutput": "keep",
                "centralGenerator": { "futureCentral": 42 }
              },
              "servers": [
                {
                  "name": "srv01",
                  "active": true,
                  "futureServer": { "nested": "value" },
                  "checks": {
                    "futureChecks": true,
                    "services": { "enabled": true, "futureServices": ["a", "b"] }
                  }
                }
              ],
              "domains": [
                {
                  "name": "app-prod",
                  "environment": "production",
                  "servers": ["srv01"],
                  "netScalerVServer": "vs-app",
                  "healthCheck": "https",
                  "description": "Domain",
                  "allowNameMismatch": true,
                  "futureDomain": { "tls": "new" }
                }
              ],
              "futureFeature": { "complex": [true, null, { "value": 7 }] }
            }
            """;
        await File.WriteAllTextAsync(ConfigPath, json);
        var service = CreateService();
        service.LoadFromFile(json, ConfigPath);
        service.UpdateApplicationTitle("Neu");

        await service.SaveToFileAsync();

        using var original = JsonDocument.Parse(json);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        SameJson(original.RootElement.GetProperty("futureFeature"), saved.RootElement.GetProperty("futureFeature"));
        SameJson(original.RootElement.GetProperty("domains"), saved.RootElement.GetProperty("domains"));
        SameJson(original.RootElement.GetProperty("general").GetProperty("futureGeneral"), saved.RootElement.GetProperty("general").GetProperty("futureGeneral"));
        SameJson(original.RootElement.GetProperty("features").GetProperty("futureFeature"), saved.RootElement.GetProperty("features").GetProperty("futureFeature"));
        SameJson(original.RootElement.GetProperty("features").GetProperty("notifications").GetProperty("futureNotification"), saved.RootElement.GetProperty("features").GetProperty("notifications").GetProperty("futureNotification"));
        SameJson(original.RootElement.GetProperty("output").GetProperty("futureOutput"), saved.RootElement.GetProperty("output").GetProperty("futureOutput"));
        SameJson(original.RootElement.GetProperty("output").GetProperty("centralGenerator").GetProperty("futureCentral"), saved.RootElement.GetProperty("output").GetProperty("centralGenerator").GetProperty("futureCentral"));
        SameJson(original.RootElement.GetProperty("servers")[0].GetProperty("futureServer"), saved.RootElement.GetProperty("servers")[0].GetProperty("futureServer"));
        SameJson(original.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("futureChecks"), saved.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("futureChecks"));
        SameJson(original.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("services").GetProperty("futureServices"), saved.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("services").GetProperty("futureServices"));
        service.GetDomains().Single().Name.Should().Be("app-prod");
    }

    [Fact]
    public async Task ReplacementUpdates_PreserveUnknownFieldsFromLoadedModels()
    {
        const string json = """
            {
              "features": { "futureFeature": 1, "notifications": { "futureNotification": 2 } },
              "output": { "futureOutput": 3, "centralGenerator": { "futureCentral": 4 } },
              "servers": [{
                "name": "srv",
                "futureServer": 5,
                "serviceTypes": [{ "type": "erreichbarkeit", "futureServiceType": 9 }],
                "checks": { "futureChecks": 6, "appPools": { "futurePools": 7 } }
              }],
              "domains": [{ "name": "app", "environment": "production", "futureDomain": 8 }]
            }
            """;
        await File.WriteAllTextAsync(ConfigPath, json);
        var service = CreateService();
        service.LoadFromFile(json, ConfigPath);

        service.UpdateFeatures(new FeaturesConfig { Notifications = new NotificationsFeatureConfig() });
        service.UpdateOutputSettings(new OutputSettings { CentralGenerator = new CentralGeneratorSettings() });
        service.UpdateServer("srv", new Server
        {
            Name = "srv",
            ServiceTypes = [new ServiceType { Type = "erreichbarkeit" }],
            Checks = new ServerChecksConfig { AppPools = new AppPoolChecksConfig() }
        });
        service.UpdateDomain("app", new Domain { Name = "app", Environment = "production" });
        await service.SaveToFileAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        saved.RootElement.GetProperty("features").GetProperty("futureFeature").GetInt32().Should().Be(1);
        saved.RootElement.GetProperty("features").GetProperty("notifications").GetProperty("futureNotification").GetInt32().Should().Be(2);
        saved.RootElement.GetProperty("output").GetProperty("futureOutput").GetInt32().Should().Be(3);
        saved.RootElement.GetProperty("output").GetProperty("centralGenerator").GetProperty("futureCentral").GetInt32().Should().Be(4);
        saved.RootElement.GetProperty("servers")[0].GetProperty("futureServer").GetInt32().Should().Be(5);
        saved.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("futureChecks").GetInt32().Should().Be(6);
        saved.RootElement.GetProperty("servers")[0].GetProperty("checks").GetProperty("appPools").GetProperty("futurePools").GetInt32().Should().Be(7);
        saved.RootElement.GetProperty("domains")[0].GetProperty("futureDomain").GetInt32().Should().Be(8);
        saved.RootElement.GetProperty("servers")[0].GetProperty("serviceTypes")[0].GetProperty("futureServiceType").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task ReplacementUpdates_MatchListItemsByStableKeyAfterInsertDeleteAndReorder()
    {
        const string json = """
            {
              "servers": [{
                "name": "srv",
                "serviceTypes": [
                  { "type": "alpha", "futureServiceType": 1 },
                  { "type": "beta", "futureServiceType": 2 },
                  { "type": "gamma", "futureServiceType": 3 }
                ]
              }]
            }
            """;
        await File.WriteAllTextAsync(ConfigPath, json);
        var service = CreateService();
        service.LoadFromFile(json, ConfigPath);

        service.UpdateServer("srv", new Server
        {
            Name = "srv",
            ServiceTypes = [new ServiceType { Type = "beta" }, new ServiceType { Type = "alpha" }]
        });
        service.UpdateServer("srv", new Server
        {
            Name = "srv",
            ServiceTypes = [new ServiceType { Type = "alpha" }, new ServiceType { Type = "beta" }]
        });
        service.UpdateServer("srv", new Server
        {
            Name = "srv",
            ServiceTypes =
            [
                new ServiceType { Type = "beta" },
                new ServiceType { Type = "delta" },
                new ServiceType { Type = "alpha" }
            ]
        });
        await service.SaveToFileAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var types = saved.RootElement.GetProperty("servers")[0].GetProperty("serviceTypes");
        types[0].GetProperty("type").GetString().Should().Be("beta");
        types[0].GetProperty("futureServiceType").GetInt32().Should().Be(2);
        types[1].GetProperty("type").GetString().Should().Be("delta");
        types[1].TryGetProperty("futureServiceType", out _).Should().BeFalse();
        types[2].GetProperty("type").GetString().Should().Be("alpha");
        types[2].GetProperty("futureServiceType").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task EverySave_CreatesUniqueBackupAndKeepsOnlyTheNewestTwenty()
    {
        await File.WriteAllTextAsync(ConfigPath, "{}");
        var service = CreateService();
        service.LoadFromFile("{}", ConfigPath);
        for (var i = 0; i < 23; i++)
        {
            service.UpdateApplicationTitle("T" + i);
            await service.SaveToFileAsync();
        }

        var backups = Directory.GetFiles(Path.Combine(_directory, "backup"), "config.json.*");
        backups.Should().HaveCount(20);
        backups.Select(Path.GetFileName).Should().OnlyHaveUniqueItems();
        var newest = backups.OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).First();
        File.ReadAllText(newest).Should().Contain("T21");
    }

    [Fact]
    public async Task BackupRotation_UsesCreationTimeWhenFilenameOrderDisagrees()
    {
        await File.WriteAllTextAsync(ConfigPath, "{}");
        var backupDirectory = Path.Combine(_directory, "backup");
        Directory.CreateDirectory(backupDirectory);
        var creationBase = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var existing = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var path = Path.Combine(backupDirectory, $"config.json.20200101T000000.0000000Z-{i:000}");
            await File.WriteAllTextAsync(path, "backup-" + i);
            File.SetCreationTimeUtc(path, creationBase.AddSeconds(20 - i));
            existing.Add(path);
        }
        File.GetCreationTimeUtc(existing[0]).Should().Be(creationBase.AddSeconds(20));
        var service = CreateService();
        service.LoadFromFile("{}", ConfigPath);
        service.UpdateApplicationTitle("Aktuell");

        await service.SaveToFileAsync();

        Directory.GetFiles(backupDirectory, "config.json.*").Should().HaveCount(20);
        File.Exists(existing[0]).Should().BeTrue("it is the newest of the pre-existing backups by creation time");
        File.Exists(existing[^1]).Should().BeFalse("it is the oldest backup by creation time despite its largest name suffix");
    }

    [Fact]
    public async Task BackupFailure_IsLoggedAndDoesNotBlockSaving()
    {
        await File.WriteAllTextAsync(ConfigPath, "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "backup"), "not a directory");
        var logger = new CaptureLogger<ConfigurationService>();
        var service = CreateService(logger);
        service.LoadFromFile("{}", ConfigPath);
        service.UpdateApplicationTitle("Gespeichert");

        var act = () => service.SaveToFileAsync();

        await act.Should().NotThrowAsync();
        File.ReadAllText(ConfigPath).Should().Contain("Gespeichert");
        logger.Messages.Should().Contain(message => message.Contains("Backup"));
    }

    private static void SameJson(JsonElement expected, JsonElement actual) =>
        JsonSerializer.Serialize(expected).Should().Be(JsonSerializer.Serialize(actual));

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
}
