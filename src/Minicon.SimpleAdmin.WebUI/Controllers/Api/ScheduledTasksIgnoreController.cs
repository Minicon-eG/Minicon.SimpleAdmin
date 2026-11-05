using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// Creates Task Scheduler event ignore rules directly from an event in the server details ("Ignorieren").
/// The rule is stored in <c>features.scheduledTasks.ignoreRules</c> of the loaded config.json, scoped to
/// the server, and applied centrally by the WebUI and the notifier right away (same as
/// <see cref="EventLogIgnoreController"/>).
/// </summary>
[ApiController]
[Route("api/scheduledtasksignore")]
public sealed class ScheduledTasksIgnoreController : ControllerBase
{
    private static readonly int[] AllowedDurationDays = { 1, 7, 30, 90 };

    private readonly ConfigurationService _configService;
    private readonly StatusReaderService _statusReader;

    public ScheduledTasksIgnoreController(ConfigurationService configService, StatusReaderService statusReader)
    {
        _configService = configService;
        _statusReader = statusReader;
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Create([FromBody] CreateScheduledTaskIgnoreRequest request)
    {
        if (!_configService.IsLoaded)
            return BadRequest(new { message = "Keine Konfiguration geladen" });
        if (string.IsNullOrWhiteSpace(request.ServerId))
            return BadRequest(new { message = "Server ist erforderlich" });
        if (string.IsNullOrWhiteSpace(request.TaskPath) && request.EventId <= 0)
            return BadRequest(new { message = "Task-Pfad oder Event-ID ist erforderlich" });
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Bitte einen Grund angeben" });
        if (request.DurationDays.HasValue && request.ExpiresAt.HasValue)
            return BadRequest(new { message = "Entweder eine Dauer oder ein Enddatum angeben" });
        if (request.DurationDays.HasValue && !AllowedDurationDays.Contains(request.DurationDays.Value))
            return BadRequest(new { message = "Erlaubte Dauer: 1, 7, 30 oder 90 Tage" });
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            return BadRequest(new { message = "Das Enddatum muss in der Zukunft liegen" });

        // Server scope: only servers from config.json, stored under their config name.
        var server = _configService.GetServers().FirstOrDefault(s =>
            string.Equals(s.Name, request.ServerId.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.GetEffectiveId(), request.ServerId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (server == null)
            return NotFound(new { message = $"Server '{request.ServerId}' ist nicht konfiguriert" });

        var user = User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        var rule = new ScheduledTaskIgnoreRule
        {
            Servers = new List<string> { server.Name },
            TaskPath = string.IsNullOrWhiteSpace(request.TaskPath) ? null : request.TaskPath.Trim(),
            EventIds = request.EventId > 0 ? new List<int> { request.EventId } : new List<int>(),
            ExpiresAt = request.DurationDays.HasValue
                ? DateTime.UtcNow.AddDays(request.DurationDays.Value)
                : request.ExpiresAt?.ToUniversalTime(),
            Reason = request.Reason.Trim(),
            CreatedBy = user ?? "WebUI"
        };

        await _configService.MutateAndSaveAsync(() =>
        {
            var features = _configService.GetFeatures();
            features.ScheduledTasks.IgnoreRules.Add(rule);
            _configService.UpdateFeatures(features);
        });
        _statusReader.InvalidateCache();

        return Ok(rule);
    }
}
