using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class BizTalkInstancesTests
{
    private static readonly Guid Id = Guid.Parse("12eacb7d-189c-4ac0-851a-ef511fcd5511");
    private static object Row(string status = "Suspended (resumable)", string? application = "A&B/Ä", string? error = "Fehler", Guid? id = null) => new
    {
        Id = id ?? Id, Application = application, InstanceStatus = status, Class = "Messaging",
        ServiceType = "Send", Uri = "http://port/Ä", SuspendTime = "2026-11-19T09:00:00+02:00",
        CreationTime = "2020-01-01T00:00:00Z", ErrorCode = "0x42", ErrorDescription = error, PendingOperation = "None"
    };
    private static string Rows(params object[] rows) => JsonSerializer.Serialize(rows);
    private static Config Config() => new()
    {
        Features = new FeaturesConfig { BizTalk = new BizTalkFeatureConfig { Enabled = true } },
        Servers = [new Server { Id = "bt-id", Name = "bt-server", Active = true,
            Checks = new ServerChecksConfig { BizTalk = new ServerBizTalkConfig { Enabled = true } } }]
    };
    private static BizTalkTarget Target() => BizTalkTargets.Resolve(Config(), "bt-id", new())!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return send(request, ct); }
    }
    private static RestBizTalkInstanceProvider Provider(string json, BizTalkOptions? options = null) =>
        new(new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://bt-server/BizTalkManagementService/OperationalData/Instances", request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        })), options ?? new(), TimeProvider.System);

    [Fact]
    public async Task OfficialFields_AreMappedWithZone_AndNoCreationFallback()
    {
        var before = DateTimeOffset.UtcNow;
        var page = await Provider(Rows(Row())).QuerySuspendedAsync(Target(), new(), default);
        var row = Assert.Single(page.Items);
        Assert.Equal(Id, row.InstanceId);
        Assert.Equal("A&B/Ä", row.Application);
        Assert.Equal(BizTalkApplicationMapping.Known, row.ApplicationMapping);
        Assert.Equal("URI: http://port/Ä", row.ServiceOrPort);
        Assert.Equal("Messaging", row.Type);
        Assert.Equal(BizTalkInstanceStatus.SuspendedResumable, row.Status);
        Assert.Equal(TimeSpan.FromHours(2), row.SuspendedSince!.Value.Offset);
        Assert.Equal("0x42", row.ErrorCode);
        Assert.Equal("Fehler", row.ErrorText);
        Assert.Equal("bt-id", page.ServerId);
        Assert.Equal("REST", page.Provider);
        Assert.InRange(page.FetchedAtUtc, before, DateTimeOffset.UtcNow);
        var noSuspend = await Provider(Rows(new { Id, InstanceStatus = "Suspended", CreationTime = "2020-01-01T00:00:00Z" }))
            .QuerySuspendedAsync(Target(), new(), default);
        Assert.Null(Assert.Single(noSuspend.Items).SuspendedSince);
    }

    [Theory]
    [InlineData("Suspended (not resumable)", BizTalkInstanceStatus.SuspendedNotResumable)]
    [InlineData("SuspendedNotResumable", BizTalkInstanceStatus.SuspendedNotResumable)]
    [InlineData("Suspended (non-resumable)", BizTalkInstanceStatus.SuspendedNotResumable)]
    [InlineData("Suspended (resumable)", BizTalkInstanceStatus.SuspendedResumable)]
    [InlineData("Suspended unfamiliar", BizTalkInstanceStatus.Unknown)]
    public async Task StatusMapping_IsConservative(string status, BizTalkInstanceStatus expected)
    {
        var page = await Provider(Rows(Row(status))).QuerySuspendedAsync(Target(), new(), default);
        Assert.Equal(expected, Assert.Single(page.Items).Status);
    }

    [Fact]
    public async Task InvalidIds_AreSkippedWithWarning_AndActiveEntriesCountAgainstScanLimit()
    {
        var json = Rows(Row(id: Guid.Empty), new { Id = "invalid", InstanceStatus = "Suspended" },
            Row("Active"), Row());
        var page = await Provider(json).QuerySuspendedAsync(Target(), new(), default);
        Assert.Single(page.Items); Assert.Equal(4, page.ScannedCount); Assert.Contains("2 Einträge", page.Warning);
        page = await Provider(json, new() { MaxInstances = 3 }).QuerySuspendedAsync(Target(), new(), default);
        Assert.Empty(page.Items); Assert.True(page.Truncated); Assert.Equal(3, page.ScannedCount);
        Assert.Contains("Ergebnis begrenzt", page.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":")]
    [InlineData("[] trailing")]
    public async Task MalformedResponses_AreProviderFailures(string json) =>
        await Assert.ThrowsAsync<BizTalkProviderException>(() => Provider(json).QuerySuspendedAsync(Target(), new(), default));

    [Fact]
    public async Task EmptyArray_IsComplete()
    {
        var page = await Provider("[]").QuerySuspendedAsync(Target(), new(), default);
        Assert.Empty(page.Items); Assert.False(page.Truncated); Assert.Equal(0, page.ScannedCount);
    }

    [Fact]
    public async Task ErrorText_IsUtf8LimitedAndSearchOnlyUsesRetainedText()
    {
        var json = Rows(Row(error: new string('ä', 20000) + "hiddenmatch"));
        var page = await Provider(json).QuerySuspendedAsync(Target(), new(), default);
        var row = Assert.Single(page.Items);
        Assert.True(row.ErrorTextTruncated); Assert.Equal(32768, Encoding.UTF8.GetByteCount(row.ErrorText!));
        Assert.Contains("Suche", page.Warning);
        page = await Provider(json).QuerySuspendedAsync(Target(), new(Error: "hiddenmatch"), default);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ByteLimit_StopsGiantItem_AndMarksPartialResult()
    {
        var page = await Provider(Rows(Row(error: new string('x', 10000))), new() { MaxResponseBytes = 512 })
            .QuerySuspendedAsync(Target(), new(), default);
        Assert.True(page.Truncated); Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ScanLimit_IsNotTruncatedAtExactEnd()
    {
        var page = await Provider(Rows(Row()), new() { MaxInstances = 1 }).QuerySuspendedAsync(Target(), new(), default);
        Assert.False(page.Truncated); Assert.Single(page.Items);
    }

    [Fact]
    public async Task FilterThenStableSortThenPage_UnknownApplicationIsDistinctFromLiteral()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var json = Rows(Row(id: second), Row(application: null), Row(id: first), Row(application: "(unbekannt)"));
        var page = await Provider(json).QuerySuspendedAsync(Target(), new(0, 1, "A&B/Ä", "0X42"), default);
        Assert.Equal(first, Assert.Single(page.Items).InstanceId); Assert.True(page.HasMore);
        Assert.Equal(2, page.FilteredCountWithinLimit);
        page = await Provider(json).QuerySuspendedAsync(Target(), new(1, 1, "A&B/Ä"), default);
        Assert.Equal(second, Assert.Single(page.Items).InstanceId); Assert.False(page.HasMore);
        page = await Provider(json).QuerySuspendedAsync(Target(), new(MissingApplication: true), default);
        Assert.Null(Assert.Single(page.Items).Application);
        page = await Provider(json).QuerySuspendedAsync(Target(), new(Application: "(unbekannt)"), default);
        Assert.Equal("(unbekannt)", Assert.Single(page.Items).Application);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsAndRedirects_FailWithoutRetry(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var provider = new RestBizTalkInstanceProvider(new HttpClient(handler), new(), TimeProvider.System);
        await Assert.ThrowsAsync<BizTalkProviderException>(() => provider.QuerySuspendedAsync(Target(), new(), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationAndTimeout_AreDistinguished()
    {
        using var handler = new Handler((_, ct) => throw new OperationCanceledException(ct));
        var provider = new RestBizTalkInstanceProvider(new HttpClient(handler), new(), TimeProvider.System);
        await Assert.ThrowsAsync<TimeoutException>(() => provider.QuerySuspendedAsync(Target(), new(), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QuerySuspendedAsync(Target(), new(), cancelled.Token));
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task ScanLimit_DisposesResponseStream_AndKeepsOnlyBoundedRows()
    {
        var stream = new ObservedStream(Encoding.UTF8.GetBytes(Rows(Row(), Row(), Row())));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var provider = new RestBizTalkInstanceProvider(new HttpClient(handler), new() { MaxInstances = 1 }, TimeProvider.System);
        var page = await provider.QuerySuspendedAsync(Target(), new(), default);
        Assert.Single(page.Items); Assert.True(page.Truncated); Assert.Equal(1, page.ScannedCount);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Deadline_CancelsPendingHttpCall_WithoutRetry()
    {
        using var handler = new Handler(async (_, ct) => {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new RestBizTalkInstanceProvider(new HttpClient(handler), new(), TimeProvider.System);
        await Assert.ThrowsAsync<TimeoutException>(() => provider.QuerySuspendedAsync(Target() with { TimeoutSeconds = 1 }, new(), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Mapper_LeavesMissingZoneUnknown_AndLabelsServiceTypeFallback()
    {
        var page = await Provider(Rows(new { Id, InstanceStatus = "Suspended", ServiceType = "Messaging", SuspendTime = "2026-11-19T10:00:00" }))
            .QuerySuspendedAsync(Target(), new(), default);
        var row = Assert.Single(page.Items);
        Assert.Null(row.SuspendedSince); Assert.Equal("Servicetyp: Messaging", row.ServiceOrPort);
        Assert.Equal(BizTalkApplicationMapping.Unknown, row.ApplicationMapping);
    }

    [Fact]
    public void HttpHandler_UsesProcessCredentialsAndNoRedirectsOrCookies()
    {
        using var handler = RestBizTalkInstanceProvider.CreateHandler();
        Assert.True(handler.UseDefaultCredentials); Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Theory]
    [InlineData("http://localhost:88/Management", "http://bt-server:88/Management/")]
    [InlineData("https://bt-server/Management/", "https://bt-server/Management/")]
    [InlineData("http://127.0.0.1/Management", "http://bt-server/Management/")]
    public void Targets_RewriteWorkerLoopbackAndKeepPathPort(string input, string expected)
    {
        var config = Config(); config.Servers[0].Checks!.BizTalk!.BaseUrl = input;
        Assert.Equal(expected, BizTalkTargets.Resolve(config, "bt-id", new())!.BaseUri.AbsoluteUri);
        Assert.NotNull(BizTalkTargets.Resolve(config, "BT-SERVER", new()));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:password@bt-server/Management")]
    [InlineData("http://other-host/Management")]
    [InlineData("http://bt-server/Management?q=1")]
    [InlineData("http://bt-server/Management#x")]
    [InlineData("not a uri")]
    public void InvalidOrUnapprovedUris_AreRejected(string input)
    {
        var config = Config(); config.Servers[0].Checks!.BizTalk!.BaseUrl = input;
        Assert.Null(BizTalkTargets.Resolve(config, "bt-id", new()));
    }

    [Fact]
    public void Targets_RequireFeatureActiveServerCheckAndExplicitHostApproval()
    {
        var config = Config(); config.Servers[0].Checks!.BizTalk!.BaseUrl = "https://api-host/Management";
        var options = new BizTalkOptions { AllowedApiHosts = new() { ["bt-id"] = "api-host" } };
        Assert.NotNull(BizTalkTargets.Resolve(config, "bt-id", options));
        config.Servers[0].Active = false; Assert.Null(BizTalkTargets.Resolve(config, "bt-id", options));
        config.Servers[0].Active = true; config.Servers[0].Checks!.BizTalk!.Enabled = false;
        Assert.Null(BizTalkTargets.Resolve(config, "bt-id", options));
        config.Servers[0].Checks!.BizTalk!.Enabled = true; config.Features!.BizTalk.Enabled = false;
        Assert.Null(BizTalkTargets.Resolve(config, "bt-id", options));
        config.Features.BizTalk.Enabled = true;
        Assert.Null(BizTalkTargets.Resolve(config, "'; “injection”", options));
        config.Servers.Clear(); Assert.Null(BizTalkTargets.Resolve(config, "bt-id", options));
    }

    [Fact]
    public void Options_ApplyHostGateAndBounds()
    {
        var empty = new ConfigurationBuilder().Build();
        var defaults = BizTalkOptions.Resolve(empty, true);
        Assert.True(defaults.ControlEnabled); Assert.Equal(1000, defaults.EffectiveMaxInstances);
        Assert.Equal(8 * 1024 * 1024, defaults.EffectiveMaxResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.QueryTimeout(null));
        Assert.False(BizTalkOptions.Resolve(empty, false).ControlEnabled);
        var optIn = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["BizTalk:AllowWithoutAuthentication"] = "true" }).Build();
        Assert.True(BizTalkOptions.Resolve(optIn, false).ControlEnabled);
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["BizTalk:ControlEnabled"] = "false" }).Build();
        Assert.False(BizTalkOptions.Resolve(disabled, true).ControlEnabled);
        defaults.MaxInstances = 99999; defaults.QueryTimeoutSeconds = 999;
        Assert.Equal(5000, defaults.EffectiveMaxInstances); Assert.Equal(TimeSpan.FromSeconds(120), defaults.QueryTimeout(600));
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.QueryTimeout(2));
        defaults.MaxInstances = 0; defaults.QueryTimeoutSeconds = 0; defaults.MaxResponseBytes = 0;
        Assert.Equal(1, defaults.EffectiveMaxInstances); Assert.Equal(TimeSpan.FromSeconds(5), defaults.QueryTimeout(600));
        Assert.Equal(1, defaults.EffectiveMaxResponseBytes);
    }

    [Fact]
    public async Task Api_GatesAndValidationPreventEveryOutboundCall()
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        var provider = new Moq.Mock<IBizTalkInstanceProvider>(Moq.MockBehavior.Strict);
        var options = new BizTalkOptions { ControlEnabled = false };
        var controller = new BizTalkInstancesController(config, options, provider.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Live("bt-id")).StatusCode);
        options.ControlEnabled = true;
        Assert.IsType<BadRequestObjectResult>(await controller.Live("bt-id", offset: -1));
        Assert.IsType<BadRequestObjectResult>(await controller.Live("bt-id", pageSize: 101));
        Assert.IsType<BadRequestObjectResult>(await controller.Live("bt-id", error: new string('a', 201)));
        Assert.IsType<NotFoundObjectResult>(await controller.Live("bt-id"));
        config.LoadFromJson(JsonSerializer.Serialize(Config(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.IsType<NotFoundObjectResult>(await controller.Live("unconfigured"));
        config.Config.Features!.BizTalk.Enabled = false;
        Assert.IsType<NotFoundObjectResult>(await controller.Live("bt-id"));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Api_UsesCanonicalConfigTargetAndPassesOnlyValidatedQuery()
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        config.LoadFromJson(JsonSerializer.Serialize(Config(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var provider = new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);
        provider.Setup(p => p.QuerySuspendedAsync(It.Is<BizTalkTarget>(t => t.ServerId == "bt-id" && t.BaseUri.Host == "bt-server"),
            It.Is<BizTalkInstanceQuery>(q => q.Application == "A&B/Ä" && q.Offset == 2 && q.PageSize == 10), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BizTalkInstancePage("bt-id", "REST", DateTimeOffset.UtcNow, [], 2, 10, false, false, 0, 0, null));
        var controller = new BizTalkInstancesController(config, new(), provider.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<OkObjectResult>(await controller.Live("BT-SERVER", application: "A&B/Ä", offset: 2, pageSize: 10));
        provider.VerifyAll();
    }

    [Fact]
    public void Page_OffersOnlyValidatedConfigTargets_AndApiExposesNoMutation()
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        var data = Config(); data.Servers.Add(new Server { Name = "disabled", Active = false });
        config.LoadFromJson(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var page = new Minicon.SimpleAdmin.WebUI.Controllers.BizTalkController(config, new());
        var model = Assert.IsType<Minicon.SimpleAdmin.WebUI.ViewModels.BizTalkInstancesViewModel>(
            Assert.IsType<ViewResult>(page.Instances("bt-id", "A&B/Ä", null)).Model);
        Assert.Equal("bt-id", Assert.Single(model.Servers).Id); Assert.Equal("A&B/Ä", model.Application);
        var method = typeof(BizTalkInstancesController).GetMethod(nameof(BizTalkInstancesController.Live))!;
        Assert.Equal("live", Assert.Single(method.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>()).Template);
        Assert.DoesNotContain(typeof(BizTalkInstancesController).GetMethods(), m => m.GetCustomAttributes(typeof(HttpPostAttribute), false).Length > 0);
        Assert.Single(typeof(IBizTalkInstanceProvider).GetMethods());
    }

    [Theory]
    [InlineData(null, "bt-id", true)]
    [InlineData("BT-SECOND", "second-id", true)]
    [InlineData("second-id", "second-id", true)]
    [InlineData("unknown", null, false)]
    [InlineData("", null, false)]
    [InlineData("  ", null, false)]
    public void Page_ResolvesDeepLinksWithoutFallingBackToAnotherServer(string? reference, string? expectedId, bool enabled)
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        var data = Config();
        data.Servers.Add(new Server { Name = "bt-second", Id = "second-id", Active = true,
            Checks = new ServerChecksConfig { BizTalk = new ServerBizTalkConfig { Enabled = true } } });
        config.LoadFromJson(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var page = new Minicon.SimpleAdmin.WebUI.Controllers.BizTalkController(config, new())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var model = Assert.IsType<Minicon.SimpleAdmin.WebUI.ViewModels.BizTalkInstancesViewModel>(
            Assert.IsType<ViewResult>(page.Instances(reference, null, null)).Model);
        Assert.Equal(2, model.Servers.Count);
        Assert.Equal(expectedId, model.Server); Assert.Equal(enabled, model.Enabled);
        if (!enabled)
        {
            Assert.Equal(404, page.Response.StatusCode);
            Assert.Contains("angeforderte Server", model.DisabledReason);
        }
    }

    [Fact]
    public void Page_RejectsExplicitEmptyQueryEvenWhenMvcBindsItAsNull()
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        config.LoadFromJson(JsonSerializer.Serialize(Config(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var page = new Minicon.SimpleAdmin.WebUI.Controllers.BizTalkController(config, new())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        page.Request.QueryString = new QueryString("?server=");
        var model = Assert.IsType<Minicon.SimpleAdmin.WebUI.ViewModels.BizTalkInstancesViewModel>(
            Assert.IsType<ViewResult>(page.Instances(null, null, null)).Model);
        Assert.Null(model.Server); Assert.False(model.Enabled); Assert.Equal(404, page.Response.StatusCode);
    }

    [Theory]
    [InlineData(false, 502)]
    [InlineData(true, 504)]
    public async Task Api_ProviderFailuresHaveSafeEnvelope(bool timeout, int expected)
    {
        var config = new ConfigurationService(new ConfigurationBuilder().Build());
        config.LoadFromJson(JsonSerializer.Serialize(Config(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var provider = new Moq.Mock<IBizTalkInstanceProvider>();
        provider.Setup(p => p.QuerySuspendedAsync(Moq.It.IsAny<BizTalkTarget>(), Moq.It.IsAny<BizTalkInstanceQuery>(), Moq.It.IsAny<CancellationToken>()))
            .ThrowsAsync(timeout ? new TimeoutException() : new BizTalkProviderException("Zugriff verweigert"));
        var controller = new BizTalkInstancesController(config, new(), provider.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ObjectResult>(await controller.Live("bt-id"));
        Assert.Equal(expected, result.StatusCode);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("ExecutingAccount", json); Assert.Contains("OperationId", json); Assert.Contains("bt-server", json);
        Assert.DoesNotContain("StackTrace", json);
    }
}
