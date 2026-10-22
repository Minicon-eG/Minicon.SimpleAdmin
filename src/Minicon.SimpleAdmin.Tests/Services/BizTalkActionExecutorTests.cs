using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class BizTalkActionExecutorTests
{
    private const string BaseUrl = "http://localhost/BizTalkManagementService";

    private static BizTalkActionStore CreateStore(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "biztalk-exec-" + Guid.NewGuid().ToString("N")[..8]);
        return new BizTalkActionStore(directory, NullLogger<BizTalkActionStore>.Instance);
    }

    [Fact]
    public async Task ExecutePendingAsync_StopsApplication()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.Application,
                TargetName = "App1"
            };
            await store.RequestAsync(request);

            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().ContainSingle();
            results[0].State.Should().Be(BizTalkActionState.Succeeded);
            api.Verify(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_StopsOrchestrationWithOwningApplication()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.Orchestration,
                TargetName = "Orch1",
                TargetParent = "App1"
            };
            await store.RequestAsync(request);

            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.StopOrchestrationAsync(BaseUrl, "App1", "Orch1", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().ContainSingle();
            results[0].State.Should().Be(BizTalkActionState.Succeeded);
            api.Verify(a => a.StopOrchestrationAsync(BaseUrl, "App1", "Orch1", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_DisablesReceiveLocationWithOwningPort()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.ReceiveLocation,
                TargetName = "RL1",
                TargetParent = "Port1"
            };
            await store.RequestAsync(request);

            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.DisableReceiveLocationAsync(BaseUrl, "Port1", "RL1", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().ContainSingle();
            results[0].State.Should().Be(BizTalkActionState.Succeeded);
            api.Verify(a => a.DisableReceiveLocationAsync(BaseUrl, "Port1", "RL1", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_ApiError_IsReportedAsFailed()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.SendPort,
                TargetName = "SP1"
            };
            await store.RequestAsync(request);

            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.StopSendPortAsync(BaseUrl, "SP1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new BizTalkApiException("HTTP 500 Internal Server Error"));

            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().ContainSingle();
            results[0].State.Should().Be(BizTalkActionState.Failed);
            results[0].Error.Should().Contain("HTTP 500");

            // The result is persisted, so the action is not repeated on the next cycle.
            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_OrchestrationWithoutApplication_Fails()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.Orchestration,
                TargetName = "Orch1"
            });

            var api = new Mock<IBizTalkApiClient>();
            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().ContainSingle();
            results[0].State.Should().Be(BizTalkActionState.Failed);
            api.Verify(a => a.StopOrchestrationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_IgnoresRequestsOfOtherServers()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest
            {
                ServerId = "srv2",
                Artifact = BizTalkArtifactKind.Application,
                TargetName = "App1"
            });

            var api = new Mock<IBizTalkApiClient>();
            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var results = await executor.ExecutePendingAsync("srv1", BaseUrl);

            results.Should().BeEmpty();
            api.Verify(a => a.StopApplicationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
