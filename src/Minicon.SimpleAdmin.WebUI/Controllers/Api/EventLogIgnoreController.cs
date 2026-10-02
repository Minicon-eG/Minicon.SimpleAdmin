using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// Creates event log ignore rules directly from an event in the server details ("Ignorieren").
/// The rule is stored in <c>features.eventLog.ignoreRules</c> of the loaded config.json, scoped to
/// the server, and applied centrally by the WebUI and the notifier right away.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EventLogIgnoreController : ControllerBase
{
    private readonly ConfigurationService _configService;
    private readonly StatusReaderService _statusReader;

    public EventLogIgnoreController(ConfigurationService configService, StatusReaderService statusReader)
    {
        _configService = configService;
        _statusReader = statusReader;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEventLogIgnoreRequest request)
    {
        if (!_configService.IsLoaded)
            return BadRequest(new { message = "Keine Konfiguration geladen" });
        if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(new { message = "Server und Quelle sind erforderlich" });
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            return BadRequest(new { message = "Das Enddatum muss in der Zukunft liegen" });

        var user = User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Ignoriert über die WebUI" : request.Reason.Trim();
        var rule = new EventLogIgnoreRule
        {
            Servers = new List<string> { request.ServerId },
            Source = request.Source,
            EventIds = request.EventId > 0 ? new List<int> { request.EventId } : new List<int>(),
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            Reason = $"{reason} ({user ?? "WebUI"}, {DateTime.Now:dd.MM.yyyy HH:mm})"
        };

        await _configService.MutateAndSaveAsync(() =>
        {
            var features = _configService.GetFeatures();
            features.EventLog.IgnoreRules.Add(rule);
            _configService.UpdateFeatures(features);
        });
        _statusReader.InvalidateCache();

        return Ok(rule);
    }
}
