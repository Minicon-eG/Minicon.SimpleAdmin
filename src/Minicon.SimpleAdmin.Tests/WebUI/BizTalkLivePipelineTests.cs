using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;
using Moq;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class BizTalkLivePipelineTests
{
    [Theory]
    [InlineData("offset=abc")]
    [InlineData("offset=2147483648")]
    [InlineData("pageSize=x")]
    [InlineData("missingApplication=falsch")]
    public async Task InvalidBinding_UsesGermanFailureEnvelopeBeforeProviderCall(string invalidQuery)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(BizTalkInstancesController).Assembly);
        builder.Services.AddSingleton(new ConfigurationService(new ConfigurationBuilder().Build()));
        builder.Services.AddSingleton(new BizTalkOptions());
        var provider = new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);
        builder.Services.AddSingleton(provider.Object);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await http.GetAsync("/api/biztalk/instances/live?server=bt-id&" + invalidQuery);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(Guid.TryParse(body.RootElement.GetProperty("operationId").GetString(), out var id));
            Assert.NotEqual(Guid.Empty, id);
            var message = body.RootElement.GetProperty("message").GetString();
            Assert.Contains("Ungültige Filter oder Seitengrenzen", message);
            Assert.Contains("Zielserver: bt-id", message);
            Assert.Contains("Ausführungskonto:", message);
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("executingAccount").GetString()));
            Assert.False(body.RootElement.TryGetProperty("errors", out _));
            provider.VerifyNoOtherCalls();
        }
        finally { await app.StopAsync(); }
    }
}
