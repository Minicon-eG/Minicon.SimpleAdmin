using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

public class BizTalkActionStoreTests
{
    private static BizTalkActionStore CreateStore(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "biztalk-actions-" + Guid.NewGuid().ToString("N")[..8]);
        return new BizTalkActionStore(directory, NullLogger<BizTalkActionStore>.Instance);
    }

    [Fact]
    public async Task RequestAsync_WritesRequest_AndListsIt()
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

            var actions = await store.ListAsync("srv1");
            actions.Should().ContainSingle();
            actions[0].Request.Id.Should().Be(request.Id);
            actions[0].Request.TargetName.Should().Be("App1");
            actions[0].State.Should().Be(BizTalkActionState.Requested);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RequestAsync_WithoutServerId_Throws()
    {
        var store = CreateStore(out var dir);
        try
        {
            var act = async () => await store.RequestAsync(new BizTalkActionRequest { TargetName = "App1" });
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsOnlyRequestsWithoutResult()
    {
        var store = CreateStore(out var dir);
        try
        {
            var first = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.SendPort, TargetName = "SP1" };
            var second = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.SendPort, TargetName = "SP2" };
            await store.RequestAsync(first);
            await store.RequestAsync(second);

            await store.SaveResultAsync(new BizTalkActionResult
            {
                RequestId = first.Id,
                ServerId = "srv1",
                State = BizTalkActionState.Succeeded,
                Message = "ok"
            });

            var pending = await store.GetPendingAsync("srv1");
            pending.Should().ContainSingle();
            pending[0].Id.Should().Be(second.Id);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsRequestWithResult()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Orchestration, TargetName = "Orch1" };
            await store.RequestAsync(request);
            await store.SaveResultAsync(new BizTalkActionResult
            {
                RequestId = request.Id,
                ServerId = "srv1",
                State = BizTalkActionState.Failed,
                Message = "fehlgeschlagen",
                Error = "HTTP 500"
            });

            var view = await store.GetAsync("srv1", request.Id);
            view.Should().NotBeNull();
            view!.State.Should().Be(BizTalkActionState.Failed);
            view.Result!.Error.Should().Be("HTTP 500");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ListAsync_OnlyReturnsOwnServer()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "A" });
            await store.RequestAsync(new BizTalkActionRequest { ServerId = "srv2", Artifact = BizTalkArtifactKind.Application, TargetName = "B" });

            (await store.ListAsync("srv1")).Should().ContainSingle();
            (await store.ListAsync("srv2")).Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldFiles()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);

            var requestFile = Path.Combine(dir, "requests", "srv1", request.Id + ".json");
            File.SetLastWriteTimeUtc(requestFile, DateTime.UtcNow.AddDays(-2));

            var removed = await store.CleanupAsync(TimeSpan.FromDays(1));

            removed.Should().Be(1);
            File.Exists(requestFile).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SetActionsDirectory_SwitchesTheExchangeDirectory()
    {
        var store = CreateStore(out var dir);
        var second = Path.Combine(Path.GetTempPath(), "biztalk-actions-2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);

            store.SetActionsDirectory(second);

            (await store.ListAsync("srv1")).Should().BeEmpty();
            File.Exists(Path.Combine(dir, "requests", "srv1", request.Id + ".json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (Directory.Exists(second)) Directory.Delete(second, true);
        }
    }
}
