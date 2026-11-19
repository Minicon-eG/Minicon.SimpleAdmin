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
    [BizTalkModelValidation("Ungültige Filter oder Seitengrenzen. Offset und Seitengröße müssen ganze Zahlen sein; fehlende Anwendung muss true oder false sein.")]
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

    public sealed record ActionRequest(string? Server, string? InstanceId, BizTalkInstanceAction? Action);

    [HttpPost("action")]
    [Consumes("application/json")]
    [ValidateAntiForgeryToken]
    [BizTalkModelValidation("Ungültige Aktionsdaten. Server, Instanz-GUID und Resume/Terminate erforderlich.")]
    public async Task<IActionResult> Action([FromBody] ActionRequest request,
        [FromServices] IBizTalkInstanceControl control, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(request.Server) || request.Server.Length > 200 ||
            !Guid.TryParse(request.InstanceId, out var id) || id == Guid.Empty ||
            request.Action is not { } action || !Enum.IsDefined(action))
            return BadRequest(Failure("Ungültiger Server, Instanz-GUID oder Aktion.", request.Server));
        var user = User.Identity?.IsAuthenticated == true ? User.Identity.Name ?? "unknown" : "anonymous";
        var result = await control.ExecuteAsync(request.Server, id, action, user, ct);
        return StatusCode(result.HttpStatus, result);
    }

    [HttpGet("audit")]
    [BizTalkModelValidation("Ungültige Auditfilter. max muss eine ganze Zahl zwischen 1 und 1000 sein.")]
    public IActionResult Audit([FromServices] IBizTalkInstanceControl control, string? server = null, string? instanceId = null, int max = 200)
    {
        Response.Headers.CacheControl = "no-store";
        if (max is < 1 or > 1000 || (instanceId != null && (!Guid.TryParse(instanceId, out var parsed) || parsed == Guid.Empty)))
            return BadRequest(Failure("Ungültige Auditfilter.", server));
        if (server != null && (string.IsNullOrWhiteSpace(server) || server.Length > 200))
            return BadRequest(Failure("Ungültiger Audit-Serverfilter.", server));
        // Audit is historical evidence, not permission to contact a target. Include disabled/removed targets
        // and unknown-target rejections without resolving any network destination.
        var known = config.IsLoaded && server != null ? config.Config.Servers.FirstOrDefault(s =>
            string.Equals(s.GetEffectiveId(), server.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, server.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
        var filter = known?.GetEffectiveId() ?? server?.Trim();
        try
        {
            var id = instanceId == null ? (Guid?)null : Guid.Parse(instanceId);
            return Ok(control.GetAudit(max, filter, id));
        }
        catch { return StatusCode(503, Failure("Datei-Audit nicht lesbar. Keine Vollständigkeit garantiert.", server)); }
    }

    // Runs before ApiController's ModelStateInvalidFilter (-2000) on each BizTalk endpoint.
    private sealed class BizTalkModelValidationAttribute : ActionFilterAttribute
    {
        public BizTalkModelValidationAttribute(string message) { this.message = message; Order = -2001; }
        private readonly string message;

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ModelState.IsValid) return;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new BadRequestObjectResult(Failure(
                message,
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
