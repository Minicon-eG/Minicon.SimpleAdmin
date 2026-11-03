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

            var requestFile = Path.Combine(dir, "requests", BizTalkActionStore.ServerDirName("srv1"), request.Id + ".json");
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
    public async Task GetAsync_WithPathTraversalInId_ReturnsNull()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" });

            // A traversal attempt must not reach a file outside the exchange directory.
            (await store.GetAsync("srv1", "../../../etc/passwd")).Should().BeNull();
            (await store.GetAsync("srv1", "..%2F..%2Fsecret")).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetAsync_WithInvalidId_ReturnsNull()
    {
        var store = CreateStore(out var dir);
        try
        {
            (await store.GetAsync("srv1", "")).Should().BeNull();
            (await store.GetAsync("srv1", "abc.def")).Should().BeNull();
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
            File.Exists(Path.Combine(dir, "requests", BizTalkActionStore.ServerDirName("srv1"), request.Id + ".json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (Directory.Exists(second)) Directory.Delete(second, true);
        }
    }

    // --- P2: collision-free server folders and server id check ---

    [Fact]
    public void ServerDirName_IsCollisionFreeForSimilarServerIds()
    {
        // "bt.prod" and "bt_prod" would both reduce to "bt_prod" without the hash.
        BizTalkActionStore.ServerDirName("bt.prod").Should().NotBe(BizTalkActionStore.ServerDirName("bt_prod"));
        BizTalkActionStore.ServerDirName("srv1").Should().Be(BizTalkActionStore.ServerDirName("srv1"));
    }

    [Fact]
    public async Task GetPendingAsync_IgnoresRequestOfAnotherServerInTheSameFolder()
    {
        var store = CreateStore(out var dir);
        try
        {
            await store.RequestAsync(new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "A" });

            // A request of a foreign server is placed into the folder of srv1 — the worker of srv1
            // must not execute it.
            var foreign = new BizTalkActionRequest { ServerId = "srv2", Artifact = BizTalkArtifactKind.Application, TargetName = "B" };
            var foreignPath = Path.Combine(dir, "requests", BizTalkActionStore.ServerDirName("srv1"), foreign.Id + ".json");
            await File.WriteAllTextAsync(foreignPath, System.Text.Json.JsonSerializer.Serialize(foreign));

            var pending = await store.GetPendingAsync("srv1");
            pending.Should().ContainSingle();
            pending[0].Id.Should().NotBe(foreign.Id);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // --- P1: exclusive claim ---

    [Fact]
    public async Task ClaimAsync_SecondWorkerGetsNothing()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);

            var first = await store.ClaimAsync("srv1", request.Id, "worker-1");
            var second = await store.ClaimAsync("srv1", request.Id, "worker-2");

            first.Should().NotBeNull();
            first!.WorkerId.Should().Be("worker-1");
            second.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetPendingAsync_ExcludesClaimedRequests()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);

            (await store.GetPendingAsync("srv1")).Should().ContainSingle();

            await store.ClaimAsync("srv1", request.Id, "worker-1");

            // A claimed request must never be handed out again.
            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MarkClaimUncertainAsync_SetsTheUncertainOutcome()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);
            await store.ClaimAsync("srv1", request.Id, "worker-1");

            await store.MarkClaimUncertainAsync("srv1", request.Id);

            var view = await store.GetAsync("srv1", request.Id);
            view.Should().NotBeNull();
            view!.Claim.Should().NotBeNull();
            view.Claim!.OutcomeUncertain.Should().BeTrue();
            view.State.Should().Be(BizTalkActionState.Uncertain);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetAsync_WithoutClaim_ReturnsRequestedState()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App1" };
            await store.RequestAsync(request);

            var view = await store.GetAsync("srv1", request.Id);
            view!.State.Should().Be(BizTalkActionState.Requested);
            view.Claim.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // --- unreadable claims: a claim file that exists always blocks the request ---

    private static string ClaimPath(string dir, string serverId, string requestId)
    {
        var folder = Path.Combine(dir, "claims", BizTalkActionStore.ServerDirName(serverId));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, requestId + ".json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"requestId\": \"abc")]
    public async Task UnreadableClaim_BlocksTheRequest_AndTurnsUncertainWhenStale(string content)
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(request);
            var path = ClaimPath(dir, "srv1", request.Id);
            await File.WriteAllTextAsync(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-20));

            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
            (await store.ClaimAsync("srv1", request.Id, "second")).Should().BeNull();

            var view = await store.GetAsync("srv1", request.Id);
            view!.Claim!.Unreadable.Should().BeTrue();
            view.State.Should().Be(BizTalkActionState.Uncertain);
            (await store.ListAsync("srv1")).Single().State.Should().Be(BizTalkActionState.Uncertain);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FreshUnreadableClaim_IsShownAsRunning()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(request);
            await File.WriteAllTextAsync(ClaimPath(dir, "srv1", request.Id), string.Empty);

            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
            (await store.GetAsync("srv1", request.Id))!.State.Should().Be(BizTalkActionState.Running);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ClaimAborted_WhileWriting_KeepsTheRequestBlocked()
    {
        // The worker stops after the claim file was created but before it was written
        // (here: cancelled). The request must not be handed out again.
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(request);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var claim = () => store.ClaimAsync("srv1", request.Id, "first", cts.Token);
            await claim.Should().ThrowAsync<OperationCanceledException>();

            File.Exists(ClaimPath(dir, "srv1", request.Id)).Should().BeTrue();
            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
            (await store.ClaimAsync("srv1", request.Id, "second")).Should().BeNull();
            (await store.GetAsync("srv1", request.Id))!.Claim!.Unreadable.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // --- server separation: folders are only an address, the file content is authoritative ---

    private const string CollidingServerA = "bt.._._._._.__._._____prod";
    private const string CollidingServerB = "bt..____._..___.__.___prod";

    [Fact]
    public void ServerDirName_SeparatesIdsWhoseShortHashCollides()
    {
        // Both ids reduce to the same readable name and share the SHA-256 prefix fce38590.
        BizTalkActionStore.ServerDirName(CollidingServerA)
            .Should().NotBe(BizTalkActionStore.ServerDirName(CollidingServerB));
    }

    [Fact]
    public async Task CollidingServer_DoesNotSeeTheForeignRequest()
    {
        var store = CreateStore(out var dir);
        try
        {
            var request = new BizTalkActionRequest { ServerId = CollidingServerA, Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(request);

            (await store.GetPendingAsync(CollidingServerB)).Should().BeEmpty();
            (await store.ListAsync(CollidingServerB)).Should().BeEmpty();
            (await store.GetAsync(CollidingServerB, request.Id)).Should().BeNull();
            (await store.GetAsync(CollidingServerA, request.Id)).Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ForeignFilesInTheServerFolder_AreNotAttributedToTheServer()
    {
        // Simulates a shared folder (old layout, manual copy): the files of srv2 lie in the
        // folder of srv1. Request, claim and result must all be checked against their owner.
        var store = CreateStore(out var dir);
        try
        {
            var foreign = new BizTalkActionRequest { ServerId = "srv2", Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(foreign);
            await store.SaveResultAsync(new BizTalkActionResult { ServerId = "srv2", RequestId = foreign.Id, State = BizTalkActionState.Succeeded });

            foreach (var root in new[] { "requests", "results" })
            {
                var from = Path.Combine(dir, root, BizTalkActionStore.ServerDirName("srv2"), foreign.Id + ".json");
                var to = Path.Combine(dir, root, BizTalkActionStore.ServerDirName("srv1"), foreign.Id + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
            }

            (await store.GetAsync("srv1", foreign.Id)).Should().BeNull();
            (await store.GetPendingAsync("srv1")).Should().BeEmpty();

            // An own request with a foreign result and claim: the result is ignored, the claim
            // still blocks the request (conservative).
            var own = new BizTalkActionRequest { ServerId = "srv1", Artifact = BizTalkArtifactKind.Application, TargetName = "App" };
            await store.RequestAsync(own);
            var resultDir = Path.Combine(dir, "results", BizTalkActionStore.ServerDirName("srv1"));
            await File.WriteAllTextAsync(Path.Combine(resultDir, own.Id + ".json"),
                $"{{\"requestId\":\"{own.Id}\",\"serverId\":\"srv2\",\"state\":\"succeeded\"}}");
            await File.WriteAllTextAsync(ClaimPath(dir, "srv1", own.Id),
                $"{{\"requestId\":\"other\",\"serverId\":\"srv1\",\"workerId\":\"w\",\"claimedAt\":\"{DateTime.UtcNow:O}\"}}");

            var view = await store.GetAsync("srv1", own.Id);
            view!.Result.Should().BeNull();
            view.Claim!.Unreadable.Should().BeTrue();
            view.State.Should().Be(BizTalkActionState.Running);
            (await store.GetPendingAsync("srv1")).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
