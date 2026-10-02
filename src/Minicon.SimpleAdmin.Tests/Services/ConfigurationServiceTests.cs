using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Services;
using Microsoft.Extensions.Configuration;

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
}
