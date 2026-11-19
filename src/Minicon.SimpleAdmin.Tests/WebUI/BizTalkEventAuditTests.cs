using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services.Control;
using Moq;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class BizTalkEventAuditTests
{
    private sealed class CaptureLogger : ILogger<WindowsActionEventAudit>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
    private static BizTalkAuditEntry Entry(BizTalkActionOutcome outcome = BizTalkActionOutcome.Accepted) => new(Guid.NewGuid(), "Completed", DateTimeOffset.UtcNow,
        "DOMAIN\\Benutzer", "DOMAIN\\Dienst", "bt-id", Guid.NewGuid(), "Übertragung", BizTalkApplicationMapping.Known, "REST", BizTalkInstanceAction.Terminate, outcome, "Angenommen");
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    [Theory]
    [InlineData(BizTalkActionOutcome.Accepted, 3100)] [InlineData(BizTalkActionOutcome.Rejected, 3101)]
    [InlineData(BizTalkActionOutcome.Unknown, 3101)] [InlineData(BizTalkActionOutcome.Failed, 3102)]
    public async Task PreferredSource_ReceivesExactlyOneStructuredResult(BizTalkActionOutcome outcome, int eventId)
    {
        var sink = new Mock<IWindowsEventSink>(); sink.SetupGet(s => s.IsWindows).Returns(true);
        sink.Setup(s => s.IsApplicationSource(It.IsAny<string>())).Returns(true);
        var writes = new ConcurrentQueue<(string source,int id,string message)>();
        sink.Setup(s=>s.Write(It.IsAny<string>(),It.IsAny<int>(),It.IsAny<string>())).Callback<string,int,string>((s,i,m)=>writes.Enqueue((s,i,m)));
        using var worker=new WindowsActionEventAudit(sink.Object,NullLogger<WindowsActionEventAudit>.Instance);
        await worker.StartAsync(default); var entry=Entry(outcome); worker.TryWrite(entry); await Until(()=>writes.Count==1); await worker.StopAsync(default);
        var write=Assert.Single(writes); Assert.Equal("Minicon.SimpleAdmin.WebUI",write.source); Assert.Equal(eventId,write.id);
        Assert.Contains(entry.OperationId.ToString(),write.message); Assert.Contains(entry.InstanceId.ToString(),write.message);
        using var json=System.Text.Json.JsonDocument.Parse(write.message);
        Assert.Equal(entry.User,json.RootElement.GetProperty("User").GetString()); Assert.Equal(entry.Application,json.RootElement.GetProperty("Application").GetString());
        Assert.Equal(entry.Server,json.RootElement.GetProperty("Server").GetString()); Assert.Equal(entry.ExecutingAccount,json.RootElement.GetProperty("ExecutingAccount").GetString());
        Assert.Equal("Terminate",json.RootElement.GetProperty("Action").GetString());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FallbackSource_AfterMissingOrUnwritablePreferred(bool writeFails)
    {
        var sink=new Mock<IWindowsEventSink>(); sink.SetupGet(s=>s.IsWindows).Returns(true);
        sink.Setup(s=>s.IsApplicationSource("Minicon.SimpleAdmin.WebUI")).Returns(writeFails);
        sink.Setup(s=>s.IsApplicationSource("SimpleAdmin")).Returns(true);
        if(writeFails) sink.Setup(s=>s.Write("Minicon.SimpleAdmin.WebUI",It.IsAny<int>(),It.IsAny<string>())).Throws<UnauthorizedAccessException>();
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Setup(s=>s.Write("SimpleAdmin",It.IsAny<int>(),It.IsAny<string>())).Callback(()=>done.TrySetResult());
        using var worker=new WindowsActionEventAudit(sink.Object,NullLogger<WindowsActionEventAudit>.Instance); await worker.StartAsync(default);worker.TryWrite(Entry());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); await worker.StopAsync(default);
    }
    [Fact] public async Task NonWindows_NeverCallsEventApi_AndDoesNotWarn()
    {
        var sink=new Mock<IWindowsEventSink>(MockBehavior.Strict); sink.SetupGet(s=>s.IsWindows).Returns(false);
        var logger=new CaptureLogger(); using var worker=new WindowsActionEventAudit(sink.Object,logger);
        await worker.StartAsync(default); await Until(()=>sink.Invocations.Any(i=>i.Method.Name=="get_IsWindows")); worker.TryWrite(Entry()); await worker.StopAsync(default);
        sink.VerifyGet(s=>s.IsWindows,Times.AtLeastOnce); sink.VerifyNoOtherCalls(); Assert.Empty(logger.Messages);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingSourcesOrWriteExceptions_DegradeWithSingleWarning(bool writeFails)
    {
        var sink=new Mock<IWindowsEventSink>();sink.SetupGet(s=>s.IsWindows).Returns(true);sink.Setup(s=>s.IsApplicationSource(It.IsAny<string>())).Returns(writeFails);
        sink.Setup(s=>s.Write(It.IsAny<string>(),It.IsAny<int>(),It.IsAny<string>())).Throws<UnauthorizedAccessException>();
        var logger=new CaptureLogger(); using var worker=new WindowsActionEventAudit(sink.Object,logger);
        await worker.StartAsync(default);for(var i=0;i<5;i++)worker.TryWrite(Entry());
        await Until(()=>!logger.Messages.IsEmpty);worker.TryWrite(Entry());await worker.StopAsync(default);
        Assert.Single(logger.Messages);
    }
    [Fact] public async Task HangingSinkAndFullQueue_NeverBlockEnqueueOrShutdown()
    {
        var sink=new Mock<IWindowsEventSink>();sink.SetupGet(s=>s.IsWindows).Returns(true);sink.Setup(s=>s.IsApplicationSource(It.IsAny<string>())).Returns(true);
        using var release=new ManualResetEventSlim();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Setup(s=>s.Write(It.IsAny<string>(),It.IsAny<int>(),It.IsAny<string>())).Callback(()=>{started.TrySetResult();release.Wait();});
        var logger=new CaptureLogger(); using var worker=new WindowsActionEventAudit(sink.Object,logger,1);
        try
        {
            await worker.StartAsync(default);worker.TryWrite(Entry());await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var enqueue=Task.Run(()=>{for(var i=0;i<100;i++)worker.TryWrite(Entry());}); await enqueue.WaitAsync(TimeSpan.FromSeconds(1));
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(1));await Until(()=>!logger.Messages.IsEmpty);Assert.Single(logger.Messages);
        }
        finally {release.Set();}
    }
    [Fact]
    public async Task Review_PlatformAndSinkNeverRunInRequestContext_OverflowDegradesOnce()
    {
        var requestContext = new AsyncLocal<bool>();
        var sink = new Mock<IWindowsEventSink>();
        sink.SetupGet(s => s.IsWindows).Returns(() => { Assert.False(requestContext.Value); return true; });
        sink.Setup(s => s.IsApplicationSource(It.IsAny<string>())).Returns(() => { Assert.False(requestContext.Value); return true; });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Setup(s => s.Write(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Callback(() =>
        {
            Assert.False(requestContext.Value); entered.TrySetResult(); release.Wait();
        });
        var logger = new CaptureLogger();
        using var worker = new WindowsActionEventAudit(sink.Object, logger, 1);
        try
        {
            await worker.StartAsync(default);
            requestContext.Value = true;
            worker.TryWrite(Entry());
            requestContext.Value = false;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requestContext.Value = true;
            for (var i = 0; i < 20; i++) worker.TryWrite(Entry());
            requestContext.Value = false;
            await Until(() => !logger.Messages.IsEmpty);
            sink.VerifyGet(s => s.IsWindows, Times.Once);
            Assert.Single(logger.Messages);
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { requestContext.Value = false; release.Set(); }
    }
}
