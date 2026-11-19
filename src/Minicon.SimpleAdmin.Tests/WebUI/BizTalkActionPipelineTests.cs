using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;
using Moq;

namespace Minicon.SimpleAdmin.Tests.WebUI;
public sealed class BizTalkActionPipelineTests
{
    [Theory]
    [InlineData("none",400)] [InlineData("foreign",400)] [InlineData("form",415)]
    [InlineData("invalid-guid",400)] [InlineData("empty-guid",400)] [InlineData("enum",400)] [InlineData("number-enum",400)]
    [InlineData("valid",200)]
    public async Task JsonCsrfAndValidation_ThroughRealMvc(string mode,int status)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(BizTalkInstancesController).Assembly);
        builder.Services.AddAntiforgery(o=>o.HeaderName="RequestVerificationToken");
        builder.Services.AddSingleton(new ConfigurationService(new ConfigurationBuilder().Build()));builder.Services.AddSingleton(new BizTalkOptions());
        var provider=new Mock<IBizTalkInstanceProvider>(MockBehavior.Strict);builder.Services.AddSingleton(provider.Object);
        var control=new Mock<IBizTalkInstanceControl>(MockBehavior.Strict);
        var id=Guid.NewGuid();
        control.Setup(c=>c.ExecuteAsync("bt-id",id,BizTalkInstanceAction.Terminate,"normal-user",It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BizTalkActionResult(Guid.NewGuid(),"bt-id",id,BizTalkInstanceAction.Terminate,BizTalkActionOutcome.Accepted,"Angenommen","account",true,null,200));
        builder.Services.AddSingleton(control.Object);
        await using var app=builder.Build();
        app.Use(async(ctx,next)=>{ctx.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"normal-user")],"test"));await next();});
        app.MapGet("/token",(HttpContext ctx,IAntiforgery csrf)=>Results.Json(new{token=csrf.GetAndStoreTokens(ctx).RequestToken})); app.MapControllers();await app.StartAsync();
        try
        {
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(address)};
            using var tokenResponse=await http.GetAsync("/token");using var token=JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/biztalk/instances/action");
            if(mode!="none" && mode!="foreign") request.Headers.Add("RequestVerificationToken",token.RootElement.GetProperty("token").GetString());
            if(mode=="foreign")request.Headers.Add("Origin","https://foreign.invalid");
            if(mode=="form") request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"server","bt-id"}});
            else request.Content=JsonContent.Create(new{server="bt-id",instanceId=mode=="invalid-guid"?"'; DROP":mode=="empty-guid"?Guid.Empty.ToString():id.ToString(),action=mode=="enum"?"Suspend":"Terminate"});
            if(mode=="number-enum") request.Content=new StringContent($"{{\"server\":\"bt-id\",\"instanceId\":\"{id}\",\"action\":99}}",System.Text.Encoding.UTF8,"application/json");
            using var response=await http.SendAsync(request);Assert.Equal(status,(int)response.StatusCode);
            control.Verify(c=>c.ExecuteAsync(It.IsAny<string>(),It.IsAny<Guid>(),It.IsAny<BizTalkInstanceAction>(),It.IsAny<string>(),It.IsAny<CancellationToken>()),mode=="valid"?Times.Once:Times.Never);
            provider.VerifyNoOtherCalls();
        }
        finally{await app.StopAsync();}
    }
}
