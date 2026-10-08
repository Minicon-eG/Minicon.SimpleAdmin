using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class StatusReaderServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<RuntimeStatusStore>> _statusStoreLogger = new();
    private readonly Mock<ILogger<AcknowledgeService>> _acknowledgeLogger = new();
    private readonly Mock<ILogger<StatusReaderService>> _statusReaderLogger = new();
    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task GetAllServersAsync_AppliesAcknowledgesOnlyToTheMatchingServer()
    {
        var statusStore = new RuntimeStatusStore(_tempDirectory, _statusStoreLogger.Object);
        var acknowledgeService = new AcknowledgeService(_tempDirectory, _acknowledgeLogger.Object);
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        var reader = new StatusReaderService(_tempDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        await statusStore.SaveStatusAsync(CreateUnhealthyStatus("server-a"), 10);
        await statusStore.SaveStatusAsync(CreateUnhealthyStatus("server-b"), 10);

        await acknowledgeService.CreateAcknowledgeAsync("server-a", "cpu_usage_critical", "tester", "Investigating", 30);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        var serverA = servers.Single(s => s.ServerId == "server-a");
        serverA.OverallStatus.Should().Be(ServiceStatus.Acknowledged);
        serverA.ActiveProblems.Should().ContainSingle(p => p.Id == "cpu_usage_critical" && p.Acknowledged);
        serverA.Services.Should().ContainSingle(s => s.Status == ServiceStatus.Acknowledged);

        var serverB = servers.Single(s => s.ServerId == "server-b");
        serverB.OverallStatus.Should().Be(ServiceStatus.Unhealthy);
        serverB.ActiveProblems.Should().ContainSingle(p => p.Id == "cpu_usage_critical" && !p.Acknowledged);
        serverB.Services.Should().ContainSingle(s => s.Status == ServiceStatus.Unhealthy);
    }

    [Fact]
    public async Task GetAllServersAsync_LoadsRuntimeStatusesFromCentralOutputStatusSubdirectory()
    {
        var defaultStatusDirectory = Path.Combine(_tempDirectory, "webui-status");
        var centralOutputDirectory = Path.Combine(_tempDirectory, "central-output");
        var centralStatusDirectory = Path.Combine(centralOutputDirectory, "status");

        Directory.CreateDirectory(centralStatusDirectory);

        var acknowledgeService = new AcknowledgeService(defaultStatusDirectory, _acknowledgeLogger.Object);
        var configurationService = CreateConfigurationWithCentralOutputPath(centralOutputDirectory);
        var reader = new StatusReaderService(defaultStatusDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        await WriteRuntimeStatusAsync(Path.Combine(centralStatusDirectory, "server-a.api.json"), CreateUnhealthyStatus("server-a"));

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].ServerId.Should().Be("server-a");
        servers[0].OverallStatus.Should().Be(ServiceStatus.Unhealthy);
    }

    [Fact]
    public async Task GetAllServersAsync_FallsBackToCombinedCentralStatusFiles()
    {
        var defaultStatusDirectory = Path.Combine(_tempDirectory, "webui-status");
        var centralOutputDirectory = Path.Combine(_tempDirectory, "central-output");

        Directory.CreateDirectory(centralOutputDirectory);

        var acknowledgeService = new AcknowledgeService(defaultStatusDirectory, _acknowledgeLogger.Object);
        var configurationService = CreateConfigurationWithCentralOutputPath(centralOutputDirectory);
        var reader = new StatusReaderService(defaultStatusDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        var combinedStatus = new CentralServerStatus
        {
            Hostname = "central-a",
            GeneratedAt = DateTime.UtcNow,
            Statuses = new List<RuntimeStatus> { CreateUnhealthyStatus("server-a") }
        };

        await File.WriteAllTextAsync(
            Path.Combine(centralOutputDirectory, "central-a.status.json"),
            JsonSerializer.Serialize(combinedStatus, StatusJsonOptions));

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].ServerId.Should().Be("server-a");
        servers[0].OverallStatus.Should().Be(ServiceStatus.Unhealthy);
    }

    [Fact]
    public async Task GetAllServersAsync_MergesLocalSingleFilesWithCombinedFilesInSameFolder()
    {
        // Central host: its own worker writes <host>.<type>.json into the same folder the other
        // servers push their <host>.status.json to — the UI must show all servers, not only the local one.
        var folder = Path.Combine(_tempDirectory, "shared-status");
        Directory.CreateDirectory(folder);

        var acknowledgeService = new AcknowledgeService(folder, _acknowledgeLogger.Object);
        var reader = new StatusReaderService(folder, acknowledgeService, new ConfigurationService(new ConfigurationBuilder().Build()), _statusReaderLogger.Object);

        await WriteRuntimeStatusAsync(Path.Combine(folder, "central.transfer.json"), CreateUnhealthyStatus("central"));
        foreach (var host in new[] { "server-a", "server-b" })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, $"{host}.status.json"), JsonSerializer.Serialize(new CentralServerStatus
            {
                Hostname = host,
                GeneratedAt = DateTime.UtcNow,
                Statuses = new List<RuntimeStatus> { CreateUnhealthyStatus(host) }
            }, StatusJsonOptions));
        }

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Select(s => s.ServerId).Should().BeEquivalentTo("central", "server-a", "server-b");
    }

    [Fact]
    public async Task GetAllServersAsync_SameServerInSeveralSources_NewestWins()
    {
        var folder = Path.Combine(_tempDirectory, "dup-status");
        Directory.CreateDirectory(folder);

        var acknowledgeService = new AcknowledgeService(folder, _acknowledgeLogger.Object);
        var reader = new StatusReaderService(folder, acknowledgeService, new ConfigurationService(new ConfigurationBuilder().Build()), _statusReaderLogger.Object);

        var old = CreateUnhealthyStatus("server-a");
        old.LastUpdate = DateTime.UtcNow.AddHours(-1);
        await WriteRuntimeStatusAsync(Path.Combine(folder, "server-a.api.json"), old);

        var fresh = CreateUnhealthyStatus("server-a");
        fresh.LastUpdate = DateTime.UtcNow;
        fresh.Status = ServiceStatus.Healthy;
        fresh.Metrics[0].Value = 20;
        fresh.Metrics[0].Status = Minicon.SimpleAdmin.Models.Status.MetricStatus.Ok;
        await File.WriteAllTextAsync(Path.Combine(folder, "server-a.status.json"), JsonSerializer.Serialize(new CentralServerStatus
        {
            Hostname = "server-a",
            GeneratedAt = fresh.LastUpdate,
            Statuses = new List<RuntimeStatus> { fresh }
        }, StatusJsonOptions));

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].Services.Should().ContainSingle();
        servers[0].OverallStatus.Should().Be(ServiceStatus.Healthy);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static ConfigurationService CreateConfigurationWithServers(bool pull = true)
    {
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        configurationService.LoadFromJson(JsonSerializer.Serialize(new
        {
            output = new { pullStatusOverHttp = pull },
            servers = new object[]
            {
                new { name = "server-a", active = true, baseUrl = "https://server-a.test", serviceTypes = new[] { new { type = "api" } } },
                new { name = "server-b", active = true, baseUrl = "https://server-b.test/", serviceTypes = new[] { new { type = "api" } } },
                new { name = "server-c", active = false, baseUrl = "https://server-c.test", serviceTypes = new[] { new { type = "api" } } }
            }
        }));
        return configurationService;
    }

    [Fact]
    public async Task GetAllServersAsync_PullsStatusOverHttp_AndMarksUnreachableServers()
    {
        var folder = Path.Combine(_tempDirectory, "pull-status");
        Directory.CreateDirectory(folder);
        var handler = new StubHandler(req => req.RequestUri!.Host == "server-a.test"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new CentralServerStatus
                {
                    Hostname = "server-a",
                    GeneratedAt = DateTime.UtcNow,
                    Statuses = new List<RuntimeStatus> { CreateUnhealthyStatus("server-a") }
                }))
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var reader = new StatusReaderService(folder, new AcknowledgeService(folder, _acknowledgeLogger.Object),
            CreateConfigurationWithServers(), _statusReaderLogger.Object, new HttpClient(handler));

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        handler.Requests.Should().BeEquivalentTo(
            "https://server-a.test/status/server-a.status.json",
            "https://server-b.test/status/server-b.status.json"); // inactive server-c is not fetched
        servers.Single(s => s.ServerId == "server-a").OverallStatus.Should().Be(ServiceStatus.Unhealthy);
        servers.Single(s => s.ServerId == "server-b").Services.Should().ContainSingle(s => s.Service == StatusReaderService.UnreachableServiceType);
    }

    [Fact]
    public async Task GetAllServersAsync_PullDisabled_DoesNotCallHttp()
    {
        var folder = Path.Combine(_tempDirectory, "pull-off");
        Directory.CreateDirectory(folder);
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var reader = new StatusReaderService(folder, new AcknowledgeService(folder, _acknowledgeLogger.Object),
            CreateConfigurationWithServers(pull: false), _statusReaderLogger.Object, new HttpClient(handler));

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        handler.Requests.Should().BeEmpty();
        servers.Should().BeEmpty();
    }

    [Fact]
    public void GetDiagnostics_NoConfigAndMissingDirectory_ExplainsBoth()
    {
        var folder = Path.Combine(_tempDirectory, "does-not-exist");
        var reader = new StatusReaderService(folder, new AcknowledgeService(_tempDirectory, _acknowledgeLogger.Object),
            new ConfigurationService(new ConfigurationBuilder().Build()), _statusReaderLogger.Object);

        var diagnostics = reader.GetDiagnostics();

        diagnostics.ConfigLoaded.Should().BeFalse();
        diagnostics.Directories[0].Exists.Should().BeFalse();
        diagnostics.Reasons.Should().HaveCount(2);
        diagnostics.Reasons[0].Should().Contain("config.json wurde nicht geladen");
        diagnostics.Reasons[1].Should().Contain(folder).And.Contain("existiert nicht");
    }

    [Fact]
    public void GetDiagnostics_EmptyDirectoryAndNoActiveServers_ExplainsBoth()
    {
        var folder = Path.Combine(_tempDirectory, "empty");
        Directory.CreateDirectory(folder);
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        configurationService.LoadFromJson(JsonSerializer.Serialize(new
        {
            servers = new object[] { new { name = "server-a", active = false, baseUrl = "https://server-a.test" } }
        }));
        var reader = new StatusReaderService(folder, new AcknowledgeService(folder, _acknowledgeLogger.Object),
            configurationService, _statusReaderLogger.Object, new HttpClient(new StubHandler(_ => new HttpResponseMessage())));

        var diagnostics = reader.GetDiagnostics();

        diagnostics.ConfigLoaded.Should().BeTrue();
        diagnostics.ServerCount.Should().Be(1);
        diagnostics.ActiveServerCount.Should().Be(0);
        diagnostics.Reasons.Should().HaveCount(2);
        diagnostics.Reasons[0].Should().Contain("keine Statusdateien");
        diagnostics.Reasons[1].Should().Contain("keiner der 1 Server aktiv");
    }

    [Fact]
    public async Task GetDiagnostics_InvalidFilesAndPullDisabled_ExplainsBoth()
    {
        var folder = Path.Combine(_tempDirectory, "invalid");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "foo.json"), "{\"unrelated\": true}");
        await File.WriteAllTextAsync(Path.Combine(folder, "acknowledges.json"), "{}");
        var reader = new StatusReaderService(folder, new AcknowledgeService(folder, _acknowledgeLogger.Object),
            CreateConfigurationWithServers(pull: false), _statusReaderLogger.Object);

        (await reader.GetAllServersAsync(forceRefresh: true)).Should().BeEmpty();
        var diagnostics = reader.GetDiagnostics();

        diagnostics.Directories[0].JsonFileCount.Should().Be(1); // acknowledges.json is not a status source
        diagnostics.Reasons.Should().HaveCount(2);
        diagnostics.Reasons[0].Should().Contain("1 JSON-Datei(en)");
        diagnostics.Reasons[1].Should().Contain("pullStatusOverHttp = false");
    }

    private static ConfigurationService CreateConfigurationWithCentralOutputPath(string centralOutputDirectory)
    {
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        configurationService.LoadFromJson(JsonSerializer.Serialize(new
        {
            output = new
            {
                centralOutputPath = centralOutputDirectory
            }
        }));

        return configurationService;
    }

    private static async Task WriteRuntimeStatusAsync(string filePath, RuntimeStatus status)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(status, StatusJsonOptions));
    }

    [Fact]
    public async Task GetAllServersAsync_WithStoppedServiceAndNoMetrics_ReturnsUnhealthy()
    {
        var statusStore = new RuntimeStatusStore(_tempDirectory, _statusStoreLogger.Object);
        var acknowledgeService = new AcknowledgeService(_tempDirectory, _acknowledgeLogger.Object);
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        var reader = new StatusReaderService(_tempDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        var status = CreateServiceOnlyStatus("server-a", "SQLWriter", isHealthy: false);
        await statusStore.SaveStatusAsync(status, 10);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].OverallStatus.Should().Be(ServiceStatus.Unhealthy);
        servers[0].ActiveProblems.Should().ContainSingle(p => p.Type == "service");
    }

    [Fact]
    public async Task GetAllServersAsync_WithHealthyServiceAndNoMetrics_ReturnsHealthy()
    {
        var statusStore = new RuntimeStatusStore(_tempDirectory, _statusStoreLogger.Object);
        var acknowledgeService = new AcknowledgeService(_tempDirectory, _acknowledgeLogger.Object);
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        var reader = new StatusReaderService(_tempDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        var status = CreateServiceOnlyStatus("server-a", "SQLWriter", isHealthy: true);
        await statusStore.SaveStatusAsync(status, 10);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].OverallStatus.Should().Be(ServiceStatus.Healthy);
        servers[0].ActiveProblems.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllServersAsync_WithSqlQueryChecks_AggregatesSqlQueryChecksInServerStatusView()
    {
        var statusStore = new RuntimeStatusStore(_tempDirectory, _statusStoreLogger.Object);
        var acknowledgeService = new AcknowledgeService(_tempDirectory, _acknowledgeLogger.Object);
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build());
        var reader = new StatusReaderService(_tempDirectory, acknowledgeService, configurationService, _statusReaderLogger.Object);

        var status = CreateStatusWithSqlQueryChecks("server-a");
        await statusStore.SaveStatusAsync(status, 10);

        var servers = await reader.GetAllServersAsync(forceRefresh: true);

        servers.Should().ContainSingle();
        servers[0].SqlQueryChecks.Should().NotBeEmpty();
        servers[0].SqlQueryChecks.Should().ContainKey("check-1");
        servers[0].SqlQueryChecks["check-1"].CheckName.Should().Be("Database Health");
        servers[0].SqlQueryChecks["check-1"].Results.Should().HaveCount(2);
    }

    private static RuntimeStatus CreateUnhealthyStatus(string serverName)
    {
        return new RuntimeStatus
        {
            Server = serverName,
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
        };
    }

    private static RuntimeStatus CreateServiceOnlyStatus(string serverName, string serviceName, bool isHealthy)
    {
        return new RuntimeStatus
        {
            Server = serverName,
            Service = "api",
            LastUpdate = DateTime.UtcNow,
            Status = isHealthy ? ServiceStatus.Healthy : ServiceStatus.Unhealthy,
            Metrics = new List<Metric>(),
            Services = new Dictionary<string, Minicon.SimpleAdmin.Models.State.ServiceState>
            {
                {
                    serviceName, new Minicon.SimpleAdmin.Models.State.ServiceState
                    {
                        Name = serviceName,
                        DisplayName = serviceName,
                        Status = isHealthy ? "Running" : "Stopped",
                        IsHealthy = isHealthy
                    }
                }
            }
        };
    }

    private static RuntimeStatus CreateStatusWithSqlQueryChecks(string serverName)
    {
        return new RuntimeStatus
        {
            Server = serverName,
            Service = "api",
            LastUpdate = DateTime.UtcNow,
            Status = ServiceStatus.Healthy,
            Metrics = new List<Metric>(),
            SqlQueryChecks = new Dictionary<string, Minicon.SimpleAdmin.Models.State.SqlQueryCheckState>
            {
                {
                    "check-1", new Minicon.SimpleAdmin.Models.State.SqlQueryCheckState
                    {
                        CheckId = "check-1",
                        CheckName = "Database Health",
                        LastChecked = DateTime.UtcNow,
                        Status = Minicon.SimpleAdmin.Models.State.MetricStatus.Healthy,
                        ConnectionError = null,
                        Results = new List<Minicon.SimpleAdmin.Models.State.SqlQueryResult>
                        {
                            new Minicon.SimpleAdmin.Models.State.SqlQueryResult
                            {
                                QueryName = "Query1",
                                Status = Minicon.SimpleAdmin.Models.State.MetricStatus.Healthy,
                                ExecutionError = null,
                                RowCount = new Minicon.SimpleAdmin.Models.State.SqlRowCountResult
                                {
                                    ActualCount = 5,
                                    ExpectedCount = 5,
                                    Operator = "==",
                                    Passed = true
                                },
                                ColumnResults = new List<Minicon.SimpleAdmin.Models.State.SqlColumnResult>()
                            },
                            new Minicon.SimpleAdmin.Models.State.SqlQueryResult
                            {
                                QueryName = "Query2",
                                Status = Minicon.SimpleAdmin.Models.State.MetricStatus.Healthy,
                                ExecutionError = null,
                                RowCount = null,
                                ColumnResults = new List<Minicon.SimpleAdmin.Models.State.SqlColumnResult>()
                            }
                        }
                    }
                }
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
