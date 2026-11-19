using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

// Authentication/RequiredRole are enforced by the existing global fallback policy.
[ApiController]
[Route("api/biztalk/instances")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BizTalkInstancesController(
    ConfigurationService config, BizTalkOptions options, IBizTalkInstanceProvider provider) : ControllerBase
{
    [HttpGet("live")]
    [LiveQueryValidation]
    public async Task<IActionResult> Live([FromQuery] string? server, [FromQuery] string? application = null,
        [FromQuery] string? error = null, [FromQuery] int offset = 0, [FromQuery] int pageSize = 50,
        [FromQuery] bool missingApplication = false, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!options.ControlEnabled) return StatusCode(403, Failure(options.DisabledReason ?? "Live-Abfrage abgeschaltet.", server));
        if (string.IsNullOrWhiteSpace(server) || offset < 0 || pageSize is < 1 or > 100 ||
            error?.Length > 200 || application?.Length > 1024 || (missingApplication && application != null))
            return BadRequest(Failure("Ungültige Filter oder Seitengrenzen.", server));
        var target = config.IsLoaded ? BizTalkTargets.Resolve(config.Config, server, options) : null;
        if (target == null) return NotFound(Failure("Kein aktives, freigegebenes BizTalk-Ziel gefunden.", server));
        try
        {
            return Ok(await provider.QuerySuspendedAsync(target,
                new BizTalkInstanceQuery(offset, pageSize, application, error, missingApplication), ct));
        }
        catch (TimeoutException)
        {
            return StatusCode(504, Failure("Zeitgrenze der Live-Abfrage überschritten.", target.ServerName));
        }
        catch (BizTalkProviderException ex)
        {
            return StatusCode(502, Failure(ex.Message, target.ServerName));
        }
    }

    // Runs before ApiController's ModelStateInvalidFilter (-2000), scoped to this GET only.
    private sealed class LiveQueryValidationAttribute : ActionFilterAttribute
    {
        public LiveQueryValidationAttribute() => Order = -2001;

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ModelState.IsValid) return;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new BadRequestObjectResult(Failure(
                "Ungültige Filter oder Seitengrenzen. Offset und Seitengröße müssen ganze Zahlen sein; fehlende Anwendung muss true oder false sein.",
                context.HttpContext.Request.Query["server"].FirstOrDefault()));
        }
    }

    private static object Failure(string message, string? server) => new
    {
        OperationId = Guid.NewGuid(),
        Message = $"{message} Zielserver: {server?[..Math.Min(server.Length, 200)] ?? "unbekannt"}. Ausführungskonto: {Environment.UserDomainName}\\{Environment.UserName}.",
        ExecutingAccount = $"{Environment.UserDomainName}\\{Environment.UserName}"
    };
}
