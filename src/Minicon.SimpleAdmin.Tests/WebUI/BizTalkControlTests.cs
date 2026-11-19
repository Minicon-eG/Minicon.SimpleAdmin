using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services.Control;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Moq;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class BizTalkControlTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "p3b-" + Guid.NewGuid());
    private static readonly Guid Id = Guid.Parse("12eacb7d-189c-4ac0-851a-ef511fcd5511");
    private readonly ConfigurationService config = Configuration();
    private static ConfigurationService Configuration()
    {
        var result = new ConfigurationService(new ConfigurationBuilder().Build());
        result.LoadFromJson("""{"features":{"bizTalk":{"enabled":true}},"servers":[{"id":"bt-id","name":"bt-server","active":true,"checks":{"bizTalk":{"enabled":true}}}]}""");
        return result;
    }
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    private BizTalkAuditLog Audit(int cap = 20) => new(dir, cap, NullLogger.Instance) { LockAttempts = 1 };
    private static BizTalkAuditEntry Entry(Guid op, string phase = "Requested", BizTalkActionOutcome outcome = BizTalkActionOutcome.Unknown) =>
        new(op, phase, DateTimeOffset.UtcNow, "DOMAIN\\Benutzer", "DOMAIN\\Dienst", "bt-id", Id, "Übertragung", BizTalkApplicationMapping.Known, "REST", BizTalkInstanceAction.Resume, outcome, "Test");
    private static BizTalkInstance Row(BizTalkInstanceStatus status = BizTalkInstanceStatus.SuspendedResumable, string? pending = "None") =>
        new(Id, "A&B/Ä", BizTalkApplicationMapping.Known, null, "Messaging", status, status.ToString(), null, null, null, false, pending);
    private static BizTalkInstancePage Page(BizTalkInstance? row) => new("bt-id", "REST", DateTimeOffset.UtcNow, row == null ? [] : [row], 0, 1000, false, false, 1, row == null ? 0 : 1, null);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private BizTalkInstanceControl Control(IBizTalkInstanceProvider provider, IBizTalkAuditLog? audit = null, IActionEventAudit? events = null, BizTalkOptions? options = null) =>
        new(config, options ?? new(), provider, audit ?? Audit(), events ?? Mock.Of<IActionEventAudit>(), TimeProvider.System);
    private static Mock<IBizTalkInstanceProvider> Provider(BizTalkInstance? row)
    {
        var provider = new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);
        provider.Setup(p => p.QuerySuspendedAsync(It.IsAny<BizTalkTarget>(), It.IsAny<BizTalkInstanceQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Page(row));
        provider.Setup(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), Id, It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return provider;
    }

    [Theory]
    [InlineData(BizTalkInstanceAction.Resume)]
    [InlineData(BizTalkInstanceAction.Terminate)]
    public async Task FullAction_UsesFreshGetThenExactlyOnePut_WithDurableRequested(BizTalkInstanceAction action)
    {
        var audit = Audit(); var events = new Mock<IActionEventAudit>(); var calls = new List<string>();
        var provider = new RestBizTalkInstanceProvider(new HttpClient(new Handler((request, ct) =>
        {
            calls.Add(request.Method.Method);
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new { Id, Application = "Übertragung", InstanceStatus = "Suspended (resumable)", PendingOperation = "None" } })) });
            Assert.Equal($"http://bt-server/BizTalkManagementService/OperationalData/Instances/{action}/{Id:D}", request.RequestUri!.AbsoluteUri);
            var requested = Assert.Single(audit.Read()); Assert.Equal("Requested", requested.Phase);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })), new(), TimeProvider.System);
        var result = await Control(provider, audit, events.Object).ExecuteAsync("BT-SERVER", Id, action, "DOMAIN\\normal", default);
        Assert.Equal(["GET", "PUT"], calls); Assert.Equal(BizTalkActionOutcome.Accepted, result.Outcome); Assert.True(result.AuditPersisted); Assert.Null(result.ObservedStatus);
        Assert.Equal(2, audit.Read().Count); Assert.All(audit.Read(), e => { Assert.Equal("bt-id", e.Server); Assert.Equal("Übertragung", e.Application); Assert.Equal("DOMAIN\\normal", e.User); Assert.Equal(result.OperationId, e.OperationId); Assert.NotEmpty(e.ExecutingAccount); });
        events.Verify(e => e.TryWrite(It.Is<BizTalkAuditEntry>(e => e.Phase == "Completed" && e.OperationId == result.OperationId)), Times.Once);
    }

    [Theory]
    [InlineData(BizTalkInstanceStatus.SuspendedNotResumable, "None", 409)]
    [InlineData(BizTalkInstanceStatus.Unknown, "None", 409)]
    [InlineData(BizTalkInstanceStatus.SuspendedResumable, "Resume", 409)]
    public async Task InvalidState_DoesNotMutate(BizTalkInstanceStatus status, string pending, int expected)
    {
        var provider = Provider(Row(status, pending));
        var result = await Control(provider.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(expected, result.HttpStatus); Assert.Equal(BizTalkActionOutcome.Rejected, result.Outcome);
        provider.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(Audit().Read());
    }
    [Fact] public async Task MissingInstance_DoesNotMutate()
    {
        var provider = Provider(null); var result = await Control(provider.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Terminate, "user", default);
        Assert.Equal(404, result.HttpStatus); provider.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact] public async Task ChangedConfigDuringPrecheck_DoesNotMutate()
    {
        var p = Provider(Row()); p.Setup(p => p.QuerySuspendedAsync(It.IsAny<BizTalkTarget>(), It.IsAny<BizTalkInstanceQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => { config.Config.Servers[0].Active = false; return Task.FromResult(Page(Row())); });
        var result = await Control(p.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(409, result.HttpStatus); p.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData(true, 403)] [InlineData(false, 404)]
    public async Task GateAndUnknownTarget_AreAuditedWithoutProvider(bool disabled, int expected)
    {
        var p = new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);
        var result = await Control(p.Object, options: new() { ControlEnabled = !disabled }).ExecuteAsync("unknown", Id, BizTalkInstanceAction.Resume, "anonymous", default);
        Assert.Equal(expected, result.HttpStatus); Assert.True(result.AuditPersisted); Assert.Equal("anonymous", Assert.Single(Audit().Read()).User); p.VerifyNoOtherCalls();
    }
    [Fact] public async Task PrewriteFailure_BlocksMutationAndAttemptsResultEvent()
    {
        Directory.CreateDirectory(dir); await File.WriteAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json"), "CORRUPT");
        var p = Provider(Row()); var events = new Mock<IActionEventAudit>();
        var result = await Control(p.Object, events: events.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(503, result.HttpStatus); Assert.False(result.AuditPersisted); Assert.Equal("CORRUPT", await File.ReadAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json")));
        p.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
        events.Verify(e => e.TryWrite(It.IsAny<BizTalkAuditEntry>()), Times.Once);
    }
    [Fact] public async Task CompletedWriteFailure_LeavesRequestedAndBlocksNextActionUntilDrain()
    {
        var audit = Audit(); FileStream? held = null; var p = Provider(Row());
        p.Setup(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), Id, It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()))
            .Callback(() => held = new FileStream(Path.Combine(dir,"biztalk-instance-actions.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).Returns(Task.CompletedTask);
        var control = Control(p.Object, audit);
        try
        {
            var result = await control.ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
            Assert.Equal(BizTalkActionOutcome.Accepted, result.Outcome); Assert.False(result.AuditPersisted); Assert.Equal("Requested", Assert.Single(audit.Read()).Phase);
            var second = await control.ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default); Assert.Equal(503, second.HttpStatus);
            p.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), Id, It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { held?.Dispose(); }
        Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid(), "Completed", BizTalkActionOutcome.Rejected)));
        Assert.Contains(audit.Read(), e => e.Phase == "Completed" && e.Outcome == BizTalkActionOutcome.Accepted);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task TimeoutOrDisconnectAfterPut_IsUnknownAndNeverRetried(bool timeout)
    {
        var calls = 0;
        var provider = new RestBizTalkInstanceProvider(new HttpClient(new Handler((request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[]{new{Id,InstanceStatus="Suspended (resumable)"}})) });
            calls++;
            if (timeout) throw new OperationCanceledException();
            throw new HttpRequestException("connection reset");
        })), new(), TimeProvider.System);
        var result = await Control(provider).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(BizTalkActionOutcome.Unknown, result.Outcome); Assert.Equal(504, result.HttpStatus); Assert.Equal(1, calls); Assert.True(result.AuditPersisted);
    }
    [Fact] public async Task BrowserCancellationAfterRequested_DoesNotCancelMutationOrAudit()
    {
        using var browser = new CancellationTokenSource(); var p = Provider(Row());
        p.Setup(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), Id, It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>())).Returns<BizTalkTarget,Guid,BizTalkInstanceAction,CancellationToken>((_,_,_,ct) =>
        { browser.Cancel(); Assert.False(ct.IsCancellationRequested); return Task.CompletedTask; });
        var result = await Control(p.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", browser.Token);
        Assert.Equal(BizTalkActionOutcome.Accepted, result.Outcome); Assert.True(result.AuditPersisted);
    }
    [Fact] public async Task ThrowingEventAudit_CannotChangeActionOrFileResult()
    {
        var events = new Mock<IActionEventAudit>(); events.Setup(e => e.TryWrite(It.IsAny<BizTalkAuditEntry>())).Throws(new UnauthorizedAccessException());
        var result = await Control(Provider(Row()).Object, events: events.Object).ExecuteAsync("bt-id", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(BizTalkActionOutcome.Accepted, result.Outcome); Assert.True(result.AuditPersisted); Assert.Equal(2, Audit().Read().Count);
    }
    [Fact] public async Task AtomicAudit_MultipleWriters_NoLostPhases_AndOperationCapKeepsOpen()
    {
        var a = Audit(30); var b = Audit(30); a.LockAttempts = b.LockAttempts = 30; var open = Guid.NewGuid(); Assert.True(await a.AppendAsync(Entry(open)));
        var ids = Enumerable.Range(0, 15).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(async (id,i) => { var store = i%2==0?a:b; Assert.True(await store.AppendAsync(Entry(id))); Assert.True(await store.AppendAsync(Entry(id,"Completed",BizTalkActionOutcome.Accepted))); }));
        Assert.True(await Audit(10).AppendAsync(Entry(Guid.NewGuid(), "Completed", BizTalkActionOutcome.Rejected)));
        var rows = a.Read(); Assert.Equal(17,rows.Select(e=>e.OperationId).Distinct().Count()); Assert.Contains(rows,e=>e.OperationId==open);
        Assert.All(rows.Where(e=>e.OperationId!=open && e.Outcome!=BizTalkActionOutcome.Rejected).GroupBy(e=>e.OperationId), g=>Assert.Equal(2,g.Count()));
    }
    [Fact] public async Task OpenCapacity_BlocksAdditionalRequested()
    {
        var audit=Audit(10); for(var i=0;i<10;i++) Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid())));
        Assert.False(await audit.AppendAsync(Entry(Guid.NewGuid())));
        Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid(),"Completed",BizTalkActionOutcome.Rejected)));
        Assert.Equal(11,audit.Read().Count);
    }
    [Theory]
    [InlineData("invalid", "Resume")] [InlineData("00000000-0000-0000-0000-000000000000", "Terminate")]
    public async Task Controller_RejectsInvalidGuidBeforeControl(string id,string action)
    {
        var control=new Mock<IBizTalkInstanceControl>(MockBehavior.Strict);
        var controller=new BizTalkInstancesController(config,new(),Mock.Of<IBizTalkInstanceProvider>()) { ControllerContext=new(){HttpContext=new DefaultHttpContext()} };
        Assert.IsType<BadRequestObjectResult>(await controller.Action(new("bt-id",id,Enum.Parse<BizTalkInstanceAction>(action)),control.Object,default)); control.VerifyNoOtherCalls();
    }
    [Fact] public async Task SameGuidActionsSerialize_AndDifferentGuidCanProceed()
    {
        var secondId=Guid.NewGuid(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p=Provider(Row()); var queries=0;
        p.Setup(p=>p.QuerySuspendedAsync(It.IsAny<BizTalkTarget>(),It.IsAny<BizTalkInstanceQuery>(),It.IsAny<CancellationToken>())).Returns(() =>
        { Interlocked.Increment(ref queries);return Task.FromResult(Page(Row()) with {Items=[Row(),Row() with {InstanceId=secondId}]}); });
        p.Setup(p=>p.ExecuteAsync(It.IsAny<BizTalkTarget>(),Id,BizTalkInstanceAction.Resume,It.IsAny<CancellationToken>())).Returns(async()=>{entered.TrySetResult();await release.Task;});
        p.Setup(p=>p.ExecuteAsync(It.IsAny<BizTalkTarget>(),secondId,BizTalkInstanceAction.Terminate,It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var control=Control(p.Object);var first=control.ExecuteAsync("bt-id",Id,BizTalkInstanceAction.Resume,"u",default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var same=control.ExecuteAsync("BT-SERVER",Id,BizTalkInstanceAction.Terminate,"u",default);
            var other=await control.ExecuteAsync("bt-id",secondId,BizTalkInstanceAction.Terminate,"u",default).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(BizTalkActionOutcome.Accepted,other.Outcome);Assert.False(same.IsCompleted);Assert.Equal(2,queries);
            release.SetResult();await Task.WhenAll(first,same);Assert.Equal(3,queries);
        }
        finally{release.TrySetResult();}
    }
    [Fact] public void AuditIsReadableWhenControlOff_AndFiltersInvalidTargets()
    {
        var control=new Mock<IBizTalkInstanceControl>();control.Setup(c=>c.GetAudit(200,"bt-id",Id)).Returns([Entry(Guid.NewGuid())]);
        control.Setup(c=>c.GetAudit(200,"removed",null)).Returns([Entry(Guid.NewGuid()) with {Server="removed"}]);
        var controller=new BizTalkInstancesController(config,new(){ControlEnabled=false},Mock.Of<IBizTalkInstanceProvider>()){ControllerContext=new(){HttpContext=new DefaultHttpContext()}};
        var response=Assert.IsType<OkObjectResult>(controller.Audit(control.Object,"bt-id",Id.ToString(),200));
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<BizTalkAuditEntry>>(response.Value));Assert.Equal("no-store",controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<OkObjectResult>(controller.Audit(control.Object,"removed"));
        Assert.IsType<BadRequestObjectResult>(controller.Audit(control.Object,"bt-id","invalid"));
        Assert.IsType<BadRequestObjectResult>(controller.Audit(control.Object,max:1001));
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(500)]
    public async Task HttpActionErrors_FailOnceAndAudit(int status)
    {
        var count=0;
        var provider=new RestBizTalkInstanceProvider(new HttpClient(new Handler((request,ct)=>
        {
            if(request.Method==HttpMethod.Get)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new[]{new{Id,InstanceStatus="Suspended (resumable)"}}))});
            count++;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        })),new(),TimeProvider.System);
        var result=await Control(provider).ExecuteAsync("bt-id",Id,BizTalkInstanceAction.Resume,"user",default);
        Assert.Equal(BizTalkActionOutcome.Failed,result.Outcome);Assert.Equal(502,result.HttpStatus);Assert.True(result.AuditPersisted);Assert.Equal(1,count);Assert.Contains("HTTP " + status,result.Message);
    }
    [Fact] public async Task RealEventWorker_HangingAndFull_DoesNotBlockTheSameActionAndFileAudit()
    {
        var sink=new Mock<IWindowsEventSink>();sink.SetupGet(s=>s.IsWindows).Returns(true);sink.Setup(s=>s.IsApplicationSource(It.IsAny<string>())).Returns(true);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Setup(s=>s.Write(It.IsAny<string>(),It.IsAny<int>(),It.IsAny<string>())).Callback(()=>{entered.TrySetResult();release.Wait();});
        using var worker=new WindowsActionEventAudit(sink.Object,NullLogger<WindowsActionEventAudit>.Instance,1);
        try
        {
            await worker.StartAsync(default);worker.TryWrite(Entry(Guid.NewGuid(),"Completed",BizTalkActionOutcome.Accepted));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.TryWrite(Entry(Guid.NewGuid(),"Completed",BizTalkActionOutcome.Accepted)); // fill waiting slot behind hung writer
            var result=await Control(Provider(Row()).Object,events:worker).ExecuteAsync("bt-id",Id,BizTalkInstanceAction.Resume,"user",default).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(BizTalkActionOutcome.Accepted,result.Outcome);Assert.True(result.AuditPersisted);Assert.Equal(2,Audit().Read().Count);
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally {release.Set();}
    }
    [Fact] public async Task ConfigChangeDuringDurablePrewrite_IsRecheckedBeforePut()
    {
        var p=Provider(Row());var audit=new Mock<IBizTalkAuditLog>();
        audit.Setup(a=>a.AppendAsync(It.IsAny<BizTalkAuditEntry>())).Returns<BizTalkAuditEntry>(e=>
        {if(e.Phase=="Requested")config.Config.Servers[0].Active=false;return Task.FromResult(true);});
        var result=await Control(p.Object,audit.Object).ExecuteAsync("bt-id",Id,BizTalkInstanceAction.Resume,"user",default);
        Assert.Equal(409,result.HttpStatus);Assert.Equal(BizTalkActionOutcome.Rejected,result.Outcome);
        p.Verify(p=>p.ExecuteAsync(It.IsAny<BizTalkTarget>(),It.IsAny<Guid>(),It.IsAny<BizTalkInstanceAction>(),It.IsAny<CancellationToken>()),Times.Never);
        audit.Verify(a=>a.AppendAsync(It.Is<BizTalkAuditEntry>(e=>e.Phase=="Completed" && e.Outcome==BizTalkActionOutcome.Rejected)),Times.Once);
    }
    [Fact] public async Task ConcurrentCompletionFailures_AllStartedOperationsStayPendingUntilDrain()
    {
        var audit=Audit();var first=Guid.NewGuid();var second=Guid.NewGuid();
        Assert.True(await audit.AppendAsync(Entry(first)));Assert.True(await audit.AppendAsync(Entry(second)));
        using(var held=new FileStream(Path.Combine(dir,"biztalk-instance-actions.json.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            Assert.False(await audit.AppendAsync(Entry(first,"Completed",BizTalkActionOutcome.Accepted)));
            Assert.False(await audit.AppendAsync(Entry(second,"Completed",BizTalkActionOutcome.Unknown)));
            Assert.Equal(2,audit.Read().Count);
        }
        Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid(),"Completed",BizTalkActionOutcome.Rejected)));
        Assert.Contains(audit.Read(),e=>e.OperationId==first && e.Phase=="Completed");
        Assert.Contains(audit.Read(),e=>e.OperationId==second && e.Phase=="Completed");
    }
    [Theory]
    [InlineData(false, BizTalkInstanceAction.Resume)]
    [InlineData(false, BizTalkInstanceAction.Terminate)]
    [InlineData(true, BizTalkInstanceAction.Resume)]
    [InlineData(true, BizTalkInstanceAction.Terminate)]
    public async Task FinalReview_OldCorruptArchive_BlocksMutationPreservesEvidenceAndAllowsCompletion(
        bool validJson, BizTalkInstanceAction action)
    {
        var audit = Audit(10);
        var first = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 12; i++)
            Assert.True(await audit.AppendAsync(Entry(i == 0 ? first : Guid.NewGuid(), "Completed", BizTalkActionOutcome.Accepted)
                with { Timestamp = start.AddSeconds(i) }));
        // The first archive is no longer part of the active rotation window.
        var archive = Directory.GetFiles(dir, "biztalk-instance-actions.archive-*.json")
            .Single(file => File.ReadAllText(file).Contains(first.ToString()));
        var original = await File.ReadAllBytesAsync(archive);
        var alreadyStarted = Guid.NewGuid();
        Assert.True(await audit.AppendAsync(Entry(alreadyStarted)));
        var corrupt = validJson ? "[]" : "BROKEN JSON";
        await File.WriteAllTextAsync(archive, corrupt);
        // A fresh instance also detects old corruption: no integrity result may be cached across calls/restarts.
        var restarted = Audit(10);
        var provider = Provider(Row());
        var result = await Control(provider.Object, restarted).ExecuteAsync("bt-id", Id, action, "user", default);
        Assert.Equal(503, result.HttpStatus);
        Assert.Equal(BizTalkActionOutcome.Rejected, result.Outcome);
        Assert.True(result.AuditPersisted); // The rejection itself can still be recorded.
        provider.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
        using (var active = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json"))))
            Assert.DoesNotContain(active.RootElement.EnumerateArray(), e =>
                e.GetProperty("operationId").GetGuid() == result.OperationId && e.GetProperty("phase").GetString() == "Requested");
        Assert.Throws<IOException>(() => restarted.Read()); // Even valid JSON with the wrong content hash is corrupt.
        var api = new BizTalkInstancesController(config, new(), provider.Object) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(503, Assert.IsType<ObjectResult>(api.Audit(Control(provider.Object, restarted))).StatusCode);
        Assert.True(await restarted.AppendAsync(Entry(alreadyStarted, "Completed", BizTalkActionOutcome.Accepted)));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(archive));
        // Restore only in the test: all original phases plus the rejection and existing completion survive.
        await File.WriteAllBytesAsync(archive, original);
        Assert.Equal(15, restarted.Read().Count);
        Assert.Contains(restarted.Read(), e => e.OperationId == alreadyStarted && e.Phase == "Completed");
        var repaired = await Control(provider.Object, restarted).ExecuteAsync("bt-id", Id, action, "user", default);
        Assert.Equal(BizTalkActionOutcome.Accepted, repaired.Outcome);
        provider.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), Id, action, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("2026-01-15T23:30:45+02:00")]
    [InlineData("2026-07-15T23:30:45-04:00")]
    public void FinalReview_DisplayFields_UseServerLocalTimeAndExplicitOffset(string timestamp)
    {
        var value = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        var display = Minicon.SimpleAdmin.WebUI.Helpers.Display.LocalTimeWithZone(value);
        Assert.Equal(display, Minicon.SimpleAdmin.WebUI.Helpers.Display.LocalTimeWithZone(value.ToUniversalTime()));
        Assert.StartsWith(Minicon.SimpleAdmin.WebUI.Helpers.Display.LocalTimeSeconds(value.UtcDateTime), display);
        Assert.Matches(@" \(UTC[+-]\d{2}:\d{2}\)$", display);
        var row = Row() with { SuspendedSince = value };
        Assert.Equal(display, row.SuspendedSinceDisplay);
        Assert.Equal(display, (Page(row) with { FetchedAtUtc = value }).FetchedAtDisplay);
        Assert.Equal(display, (Entry(Guid.NewGuid()) with { Timestamp = value }).TimestampDisplay);
        Assert.Null(Row().SuspendedSinceDisplay);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(display, json.RootElement.GetProperty("suspendedSinceDisplay").GetString());
        Assert.Equal(value, json.RootElement.GetProperty("suspendedSince").GetDateTimeOffset());
    }

    [Fact]
    public async Task Review_CapacityReduction_AllExistingOperationsCanCompleteAndRemainQueryable()
    {
        var previous = Audit(20);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) Assert.True(await previous.AppendAsync(Entry(id)));
        var reduced = Audit(10);
        Assert.False(await reduced.AppendAsync(Entry(Guid.NewGuid())));
        foreach (var id in ids)
            Assert.True(await reduced.AppendAsync(Entry(id, "Completed", BizTalkActionOutcome.Accepted)));
        var rows = new BizTalkAuditLog(dir, 10, NullLogger.Instance).Read();
        Assert.Equal(40, rows.Count);
        foreach (var id in ids)
            Assert.Equal(2, rows.Count(e => e.OperationId == id));
        using var active = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json")));
        Assert.Equal(10, active.RootElement.EnumerateArray().Select(e => e.GetProperty("operationId").GetGuid()).Distinct().Count());
        Assert.NotEmpty(Directory.GetFiles(dir, "biztalk-instance-actions.archive-*.json"));
    }

    [Fact]
    public async Task Review_FullOpenCapacity_PreservesEveryRejectBeforeAndAfterSaturation()
    {
        var audit = Audit(10);
        var rejected = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            var op = Guid.NewGuid(); rejected.Add(op);
            Assert.True(await audit.AppendAsync(Entry(op, "Completed", BizTalkActionOutcome.Rejected)));
        }
        for (var i = 0; i < 10; i++) Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid())));
        for (var i = 0; i < 20; i++)
        {
            var op = Guid.NewGuid(); rejected.Add(op);
            Assert.True(await audit.AppendAsync(Entry(op, "Completed", BizTalkActionOutcome.Rejected)));
        }
        var provider = Provider(Row());
        var result = await Control(provider.Object, audit).ExecuteAsync("BT-SERVER", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(503, result.HttpStatus);
        Assert.True(result.AuditPersisted); // Capacity blocks the mutation, never its rejection audit.
        var rows = new BizTalkAuditLog(dir, 10, NullLogger.Instance).Read();
        Assert.Equal(71, rows.Count);
        Assert.All(rejected, id => Assert.Contains(rows, e => e.OperationId == id && e.Outcome == BizTalkActionOutcome.Rejected));
        Assert.Contains(rows, e => e.OperationId == result.OperationId && e.Server == "bt-id" && e.RequestedServer == "BT-SERVER" && e.ServerKnown == true);
        provider.Verify(p => p.ExecuteAsync(It.IsAny<BizTalkTarget>(), It.IsAny<Guid>(), It.IsAny<BizTalkInstanceAction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(404, BizTalkActionOutcome.Rejected, 404, BizTalkInstanceAction.Resume)]
    [InlineData(409, BizTalkActionOutcome.Rejected, 409, BizTalkInstanceAction.Resume)]
    [InlineData(408, BizTalkActionOutcome.Unknown, 504, BizTalkInstanceAction.Resume)]
    [InlineData(504, BizTalkActionOutcome.Unknown, 504, BizTalkInstanceAction.Resume)]
    [InlineData(404, BizTalkActionOutcome.Rejected, 404, BizTalkInstanceAction.Terminate)]
    [InlineData(409, BizTalkActionOutcome.Rejected, 409, BizTalkInstanceAction.Terminate)]
    [InlineData(408, BizTalkActionOutcome.Unknown, 504, BizTalkInstanceAction.Terminate)]
    [InlineData(504, BizTalkActionOutcome.Unknown, 504, BizTalkInstanceAction.Terminate)]
    public async Task Review_UpstreamStatus_PreservesMeaningWithoutRetry(int upstream, BizTalkActionOutcome outcome, int status, BizTalkInstanceAction action)
    {
        var calls = new List<string>();
        var provider = new RestBizTalkInstanceProvider(new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request.Method.Method);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new { Id, InstanceStatus = "Suspended (resumable)", Application = "Änderung" } })) }
                : new HttpResponseMessage((HttpStatusCode)upstream));
        })), new(), TimeProvider.System);
        var events = new Mock<IActionEventAudit>();
        var result = await Control(provider, events: events.Object).ExecuteAsync("bt-id", Id, action, "user", default);
        Assert.Equal(new[] { "GET", "PUT" }, calls);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(status, result.HttpStatus); Assert.True(result.AuditPersisted);
        Assert.Contains("HTTP " + upstream, result.Message); Assert.Null(result.ObservedStatus);
        Assert.Contains(Audit().Read(), e => e.OperationId == result.OperationId && e.Phase == "Completed" && e.Outcome == outcome);
        events.Verify(e => e.TryWrite(It.Is<BizTalkAuditEntry>(e => e.Outcome == outcome)), Times.Once);
    }

    [Theory]
    [InlineData("feature")] [InlineData("inactive")] [InlineData("check")] [InlineData("removed")] [InlineData("unloaded")]
    public async Task Review_HistoricalAudit_RemainsReadableRegardlessOfCurrentConfiguration(string change)
    {
        var audit = Audit(10);
        var archivedId = Guid.NewGuid();
        Assert.True(await audit.AppendAsync(Entry(archivedId, "Completed", BizTalkActionOutcome.Accepted)));
        // Move the sought operation into an archive, and verify filtering happens before Take(max).
        for (var i = 0; i < 15; i++) Assert.True(await audit.AppendAsync(Entry(Guid.NewGuid(), "Completed", BizTalkActionOutcome.Rejected) with { InstanceId = Guid.NewGuid() }));
        var viewConfig = config;
        switch (change)
        {
            case "feature": config.Config.Features!.BizTalk.Enabled = false; break;
            case "inactive": config.Config.Servers[0].Active = false; break;
            case "check": config.Config.Servers[0].Checks!.BizTalk!.Enabled = false; break;
            case "removed": config.Config.Servers.Clear(); break;
            case "unloaded": viewConfig = new ConfigurationService(new ConfigurationBuilder().Build()); break;
        }
        var control = new BizTalkInstanceControl(viewConfig, new() { ControlEnabled = false }, Mock.Of<IBizTalkInstanceProvider>(), audit, Mock.Of<IActionEventAudit>(), TimeProvider.System);
        var controller = new BizTalkInstancesController(viewConfig, new() { ControlEnabled = false }, Mock.Of<IBizTalkInstanceProvider>())
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<OkObjectResult>(controller.Audit(control, "BT-ID", Id.ToString(), 1));
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<BizTalkAuditEntry>>(response.Value));
        Assert.Equal(archivedId, row.OperationId);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<OkObjectResult>(controller.Audit(control));
    }

    [Fact]
    public async Task Review_RejectedUnknownTarget_RetainsRequestedTargetAndIsQueryable()
    {
        var provider = new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);
        var control = Control(provider.Object);
        var result = await control.ExecuteAsync("removed.example", Id, BizTalkInstanceAction.Resume, "user", default);
        Assert.Equal(404, result.HttpStatus); Assert.True(result.AuditPersisted);
        var controller = new BizTalkInstancesController(config, new(), provider.Object) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<OkObjectResult>(controller.Audit(control, "REMOVED.EXAMPLE"));
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<BizTalkAuditEntry>>(response.Value));
        Assert.Equal("unbekannt", row.Server); Assert.False(row.ServerKnown); Assert.Equal("removed.example", row.RequestedServer);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Review_TaskAudit_JsonNullRemainsEmpty_WithoutCorruptBackup()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ScheduledTaskControlOptions.AuditFileName);
        await File.WriteAllTextAsync(path, "null");
        var audit = new ScheduledTaskAuditLog(dir, 10, NullLogger.Instance);
        Assert.Null(await audit.AppendAsync([new ScheduledTaskAuditEntry { Timestamp = DateTime.UtcNow, User = "user", Server = "bt-server", Action = "Run", Success = true }]));
        Assert.Equal("user", Assert.Single(audit.Read(10, null)).User);
        Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
        // BizTalk deliberately keeps fail-closed null handling.
        await File.WriteAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json"), "null");
        Assert.False(await Audit().AppendAsync(Entry(Guid.NewGuid())));
        Assert.Equal("null", await File.ReadAllTextAsync(Path.Combine(dir, "biztalk-instance-actions.json")));
    }

    [Fact]
    public async Task Review_TaskAudit_RetainsOriginalShortWriteError()
    {
        await File.WriteAllTextAsync(dir, "not a directory");
        try
        {
            var expected = Assert.Throws<IOException>(() => Directory.CreateDirectory(dir)).Message;
            var audit = new ScheduledTaskAuditLog(dir, 10, NullLogger.Instance);
            var error = await audit.AppendAsync([new ScheduledTaskAuditEntry { Timestamp = DateTime.UtcNow, User = "user" }]);
            Assert.Equal(expected, error);
        }
        finally { File.Delete(dir); }
    }
}
