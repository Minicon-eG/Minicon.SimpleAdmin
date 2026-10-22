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

    [Fact]
    public async Task ExecutePendingAsync_TwoWorkersInParallel_ExecuteTheRequestOnlyOnce()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.Application,
                TargetName = "App1"
            });

            var calls = 0;
            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref calls);
                    // Give the second worker time to compete for the same request.
                    await Task.Delay(150);
                });

            var executor1 = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);
            var executor2 = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);

            var both = Task.WhenAll(
                executor1.ExecutePendingAsync("srv1", BaseUrl),
                executor2.ExecutePendingAsync("srv1", BaseUrl));

            var completed = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().Be(both, "the request must be executed by exactly one worker");

            var results = (await both).SelectMany(r => r).ToList();
            results.Should().ContainSingle();
            api.Verify(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecutePendingAsync_WhenResultCannotBeSaved_DoesNotRepeatTheAction()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest
            {
                ServerId = "srv1",
                Artifact = BizTalkArtifactKind.Application,
                TargetName = "App1"
            });

            var api = new Mock<IBizTalkApiClient>();
            api.Setup(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var executor = new BizTalkActionExecutor(api.Object, store, NullLogger<BizTalkActionExecutor>.Instance);

            // The result cannot be persisted — the outcome stays unclear.
            var failingStore = new FailingResultStore(store);
            var failingExecutor = new BizTalkActionExecutor(api.Object, failingStore, NullLogger<BizTalkActionExecutor>.Instance);

            await failingExecutor.ExecutePendingAsync("srv1", BaseUrl);
            api.Verify(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()), Times.Once);

            // The next cycle must not execute the request again.
            await executor.ExecutePendingAsync("srv1", BaseUrl);
            api.Verify(a => a.StopApplicationAsync(BaseUrl, "App1", It.IsAny<CancellationToken>()), Times.Once);

            // The request is reported as unclear instead of being silently repeated.
            var pending = await store.GetPendingAsync("srv1");
            pending.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Store that fails when the result is written (e.g. a full or unreachable share).</summary>
    private sealed class FailingResultStore : IBizTalkActionStore
    {
        private readonly IBizTalkActionStore _inner;

        public FailingResultStore(IBizTalkActionStore inner) => _inner = inner;

        public Task<BizTalkActionRequest> RequestAsync(BizTalkActionRequest request, CancellationToken cancellationToken = default)
            => _inner.RequestAsync(request, cancellationToken);

        public Task<List<BizTalkActionView>> ListAsync(string serverId, CancellationToken cancellationToken = default)
            => _inner.ListAsync(serverId, cancellationToken);

        public Task<BizTalkActionView?> GetAsync(string serverId, string requestId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(serverId, requestId, cancellationToken);

        public Task<List<BizTalkActionRequest>> GetPendingAsync(string serverId, CancellationToken cancellationToken = default)
            => _inner.GetPendingAsync(serverId, cancellationToken);

        public Task SaveResultAsync(BizTalkActionResult result, CancellationToken cancellationToken = default)
            => throw new IOException("Austauschverzeichnis nicht schreibbar.");

        public Task<BizTalkActionClaim?> ClaimAsync(string serverId, string requestId, string workerId, CancellationToken cancellationToken = default)
            => _inner.ClaimAsync(serverId, requestId, workerId, cancellationToken);

        public Task MarkClaimUncertainAsync(string serverId, string requestId, CancellationToken cancellationToken = default)
            => _inner.MarkClaimUncertainAsync(serverId, requestId, cancellationToken);

        public Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
            => _inner.CleanupAsync(maxAge, cancellationToken);
    }
}
