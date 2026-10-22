using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.Tests.WebUI;

/// <summary>
/// Tests of the BizTalk action controller: the client may only address elements that the
/// server actually reports as problems. The Management API URL and the owning application /
/// receive port are never taken from the client.
/// </summary>
public class BizTalkActionsControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));
    private readonly string _statusDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"), "status");

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BizTalkActionStore _store;
    private readonly ConfigurationService _configService;

    public BizTalkActionsControllerTests()
    {
        Directory.CreateDirectory(_statusDirectory);

        _store = new BizTalkActionStore(_directory, NullLogger<BizTalkActionStore>.Instance);

        _configService = new ConfigurationService(new ConfigurationBuilder().Build());
        _configService.LoadFromJson(JsonSerializer.Serialize(new
        {
            features = new
            {
                bizTalk = new { enabled = true, actionsEnabled = true, actionsDirectory = _directory }
            },
            servers = new object[]
            {
                new
                {
                    name = "srv1",
                    active = true,
                    checks = new { bizTalk = new { enabled = true } }
                }
            }
        }));
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_statusDirectory);
        if (root != null && Directory.Exists(root))
            Directory.Delete(root, true);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private StatusReaderService CreateStatusReader()
    {
        var acknowledgeService = new AcknowledgeService(_statusDirectory, NullLogger<AcknowledgeService>.Instance);
        return new StatusReaderService(
            _statusDirectory,
            acknowledgeService,
            _configService,
            NullLogger<StatusReaderService>.Instance);
    }

    /// <summary>Publishes the BizTalk status of srv1 (as the worker on that server would).</summary>
    private void PublishBizTalkStatus(params BizTalkArtifact[] problems)
    {
        var status = new RuntimeStatus
        {
            Server = "srv1",
            Service = "biztalk",
            LastUpdate = DateTime.UtcNow,
            Status = ServiceStatus.Unhealthy,
            BizTalk = new BizTalkState
            {
                ApiReachable = true,
                Problems = problems.ToList()
            }
        };

        var file = Path.Combine(_statusDirectory, "srv1.biztalk.json");
        File.WriteAllText(file, JsonSerializer.Serialize(status, StatusJsonOptions));
    }

    private BizTalkActionsController CreateController()
        => new(_store, _configService, CreateStatusReader(), NullLogger<BizTalkActionsController>.Instance);

    private static BizTalkArtifact Artifact(string type, string name, string? application = null, string? receivePort = null)
        => new()
        {
            ArtifactType = type,
            Name = name,
            Status = "Stopped",
            ApplicationName = application,
            ReceivePortName = receivePort
        };

    // --- P1: the client may not name an element that is not reported as a problem ---

    [Fact]
    public async Task Create_WithUnknownApplication_IsRejected()
    {
        PublishBizTalkStatus(Artifact("application", "ReportedApp"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "application",
            TargetName = "SomeOtherRunningApp"
        });

        result.Should().BeOfType<BadRequestObjectResult>();
        (await _store.ListAsync("srv1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithUnknownSendPort_IsRejected()
    {
        PublishBizTalkStatus(Artifact("sendport", "ReportedPort"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "sendport",
            TargetName = "OtherPort"
        });

        result.Should().BeOfType<BadRequestObjectResult>();
        (await _store.ListAsync("srv1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithReportedApplication_IsAccepted()
    {
        PublishBizTalkStatus(Artifact("application", "ReportedApp"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "application",
            TargetName = "ReportedApp"
        });

        result.Should().BeOfType<AcceptedResult>();
        var actions = await _store.ListAsync("srv1");
        actions.Should().ContainSingle();
        actions[0].Request.TargetName.Should().Be("ReportedApp");
    }

    [Fact]
    public async Task Create_WithReportedOrchestration_TakesTheApplicationFromTheStatus()
    {
        PublishBizTalkStatus(Artifact("orchestration", "Orch1", application: "OwningApp"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "orchestration",
            TargetName = "Orch1"
        });

        result.Should().BeOfType<AcceptedResult>();
        var actions = await _store.ListAsync("srv1");
        actions.Should().ContainSingle();
        actions[0].Request.TargetParent.Should().Be("OwningApp");
    }

    [Fact]
    public async Task Create_WithReportedReceiveLocation_TakesTheReceivePortFromTheStatus()
    {
        PublishBizTalkStatus(Artifact("receivelocation", "Loc1", receivePort: "OwningPort"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "receivelocation",
            TargetName = "Loc1"
        });

        result.Should().BeOfType<AcceptedResult>();
        var actions = await _store.ListAsync("srv1");
        actions.Should().ContainSingle();
        actions[0].Request.TargetParent.Should().Be("OwningPort");
    }

    [Fact]
    public async Task Create_WithoutBizTalkStatus_IsRejected()
    {
        // No status published at all — nothing can be assigned unambiguously.
        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "srv1",
            Artifact = "application",
            TargetName = "ReportedApp"
        });

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- bulk: every item is validated on its own ---

    [Fact]
    public async Task CreateBulk_RejectsUnknownTargetsAndKeepsTheReportedOnes()
    {
        PublishBizTalkStatus(
            Artifact("application", "ReportedApp"),
            Artifact("sendport", "ReportedPort"));

        var controller = CreateController();
        var result = await controller.CreateBulk(new BulkBizTalkActionRequest
        {
            Items = new List<CreateBizTalkActionRequest>
            {
                new() { ServerId = "srv1", Artifact = "application", TargetName = "ReportedApp" },
                new() { ServerId = "srv1", Artifact = "application", TargetName = "UnknownApp" },
                new() { ServerId = "srv1", Artifact = "sendport", TargetName = "ReportedPort" }
            }
        });

        result.Should().BeOfType<OkObjectResult>();

        var actions = await _store.ListAsync("srv1");
        actions.Should().HaveCount(2);
        actions.Select(a => a.Request.TargetName).Should().BeEquivalentTo(new[] { "ReportedApp", "ReportedPort" });
    }

    [Fact]
    public async Task Create_WithServerWithoutBizTalkCheck_IsRejected()
    {
        PublishBizTalkStatus(Artifact("application", "ReportedApp"));

        var controller = CreateController();
        var result = await controller.Create(new CreateBizTalkActionRequest
        {
            ServerId = "unknown-server",
            Artifact = "application",
            TargetName = "ReportedApp"
        });

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
