using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class AcknowledgesController : ControllerBase
{
    private static readonly JsonSerializerOptions PublishJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAcknowledgeService _acknowledgeService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AcknowledgesController> _logger;

    public AcknowledgesController(
        IAcknowledgeService acknowledgeService,
        IWebHostEnvironment environment,
        ILogger<AcknowledgesController> logger)
    {
        _acknowledgeService = acknowledgeService;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var acknowledgeState = await _acknowledgeService.LoadAcknowledgesAsync();
        var active = acknowledgeState.Acknowledges
            .Where(a => a.Status == AcknowledgeStatus.Active)
            .ToList();
        return Ok(active);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAcknowledgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) ||
            string.IsNullOrWhiteSpace(request.ProblemId) ||
            string.IsNullOrWhiteSpace(request.AcknowledgedBy) ||
            request.DurationMinutes < 0)
        {
            return BadRequest(new { message = "Invalid acknowledge request payload" });
        }

        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
        {
            return BadRequest(new { message = "Das Enddatum muss in der Zukunft liegen" });
        }

        // Prefer the authenticated Windows user over the client-supplied name.
        var acknowledgedBy = User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
            ? User.Identity.Name!
            : request.AcknowledgedBy;

        var acknowledge = await _acknowledgeService.CreateAcknowledgeAsync(
            request.ServerId,
            request.ProblemId,
            acknowledgedBy,
            request.Comment,
            request.DurationMinutes,
            request.AutoResetOnHealthy,
            request.SuppressAlerts,
            request.ExpiresAt?.ToUniversalTime());

        await PublishActiveAcknowledgesAsync();

        return CreatedAtAction(nameof(GetById), new { acknowledgeId = acknowledge.Id }, acknowledge);
    }

    [HttpGet("server/{serverId}")]
    public async Task<IActionResult> GetByServer(string serverId)
    {
        var serverAcks = await _acknowledgeService.GetAcknowledgesForServerAsync(serverId);
        var active = serverAcks.Where(a => a.Status == AcknowledgeStatus.Active).ToList();
        return Ok(active);
    }

    [HttpGet("{acknowledgeId}")]
    public async Task<IActionResult> GetById(string acknowledgeId)
    {
        var acknowledge = await _acknowledgeService.GetAcknowledgeAsync(acknowledgeId);
        if (acknowledge == null)
        {
            return NotFound(new { message = $"Acknowledge '{acknowledgeId}' not found" });
        }
        return Ok(acknowledge);
    }

    [HttpPut("{acknowledgeId}/extend")]
    public async Task<IActionResult> Extend(string acknowledgeId, [FromBody] ExtendAcknowledgeRequest request)
    {
        if (request.AdditionalMinutes <= 0)
        {
            return BadRequest(new { message = "AdditionalMinutes must be greater than 0" });
        }

        var existing = await _acknowledgeService.GetAcknowledgeAsync(acknowledgeId);
        if (existing == null)
        {
            return NotFound(new { message = $"Acknowledge '{acknowledgeId}' not found" });
        }

        await _acknowledgeService.ExtendAcknowledgeAsync(acknowledgeId, request.AdditionalMinutes);
        await PublishActiveAcknowledgesAsync();

        var updated = await _acknowledgeService.GetAcknowledgeAsync(acknowledgeId);
        return Ok(updated);
    }

    [HttpDelete("{acknowledgeId}")]
    public async Task<IActionResult> Cancel(string acknowledgeId)
    {
        var existing = await _acknowledgeService.GetAcknowledgeAsync(acknowledgeId);
        if (existing == null)
        {
            return NotFound(new { message = $"Acknowledge '{acknowledgeId}' not found" });
        }

        await _acknowledgeService.CancelAcknowledgeAsync(acknowledgeId);
        await PublishActiveAcknowledgesAsync();
        return NoContent();
    }

    /// <summary>
    /// Publishes a read-only copy of the currently active acknowledges to
    /// <c>wwwroot/status/acknowledges.json</c> so the central notifier (running on another host) can
    /// read them over HTTP. Best-effort: a failure here never fails the acknowledge mutation itself.
    /// </summary>
    private async Task PublishActiveAcknowledgesAsync()
    {
        try
        {
            var webRoot = _environment.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
                webRoot = Path.Combine(_environment.ContentRootPath, "wwwroot");

            var statusDir = Path.Combine(webRoot, "status");
            Directory.CreateDirectory(statusDir);

            var state = await _acknowledgeService.LoadAcknowledgesAsync();
            var publish = new AcknowledgeState
            {
                LastUpdated = DateTime.UtcNow,
                Acknowledges = state.Acknowledges.Where(a => a.Status == AcknowledgeStatus.Active).ToList(),
                History = new List<Acknowledge>()
            };

            var json = JsonSerializer.Serialize(publish, PublishJsonOptions);
            var finalPath = Path.Combine(statusDir, "acknowledges.json");
            var tempPath = finalPath + ".tmp";
            await System.IO.File.WriteAllTextAsync(tempPath, json);
            System.IO.File.Move(tempPath, finalPath, overwrite: true);
            _logger.LogDebug("Published {Count} active acknowledge(s) to {Path}", publish.Acknowledges.Count, finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish active acknowledges to wwwroot/status/acknowledges.json");
        }
    }
}
