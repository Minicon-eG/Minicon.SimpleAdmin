using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// Requests the deactivation of BizTalk artefacts (application / orchestration / send port /
/// receive location) from the problems list. The WebUI never talks to the BizTalk Management API
/// itself — it writes a request into a shared directory that the worker on the target server picks
/// up and executes, then reports the result back.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BizTalkActionsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BizTalkActionStore _store;
    private readonly ConfigurationService _configService;
    private readonly ILogger<BizTalkActionsController> _logger;

    public BizTalkActionsController(
        BizTalkActionStore store,
        ConfigurationService configService,
        ILogger<BizTalkActionsController> logger)
    {
        _store = store;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Points the store at the configured exchange directory. Done per request, so a change of
    /// the configuration (server settings) takes effect without a restart.
    /// </summary>
    private void EnsureActionsDirectory()
    {
        var configured = _configService.IsLoaded
            ? _configService.GetFeatures().BizTalk.ActionsDirectory
            : null;
        _store.SetActionsDirectory(configured);
    }

    /// <summary>Lists the actions of one server (newest first), including their results.</summary>
    [HttpGet("server/{serverId}")]
    public async Task<IActionResult> GetForServer(string serverId)
    {
        EnsureActionsDirectory();
        var actions = await _store.ListAsync(serverId);
        return Ok(actions);
    }

    /// <summary>Returns one action (request + result) or 404 when it does not exist.</summary>
    [HttpGet("{serverId}/{requestId}")]
    public async Task<IActionResult> Get(string serverId, string requestId)
    {
        EnsureActionsDirectory();
        var action = await _store.GetAsync(serverId, requestId);
        if (action == null)
            return NotFound(new { message = $"Aktion '{requestId}' nicht gefunden" });

        return Ok(action);
    }

    /// <summary>
    /// Requests the deactivation of a single artefact. The Management API URL and the owning
    /// application / receive port are resolved from the server's configuration and the published
    /// status — the client cannot choose them.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBizTalkActionRequest request)
    {
        EnsureActionsDirectory();

        if (string.IsNullOrWhiteSpace(request.ServerId) ||
            string.IsNullOrWhiteSpace(request.Artifact) ||
            string.IsNullOrWhiteSpace(request.TargetName))
        {
            return BadRequest(new { message = "Ungültige Anfrage: ServerId, Artifact und TargetName sind erforderlich." });
        }

        if (!Enum.TryParse<BizTalkArtifactKind>(request.Artifact, ignoreCase: true, out var artifact))
        {
            return BadRequest(new { message = $"Unbekannter Artefakt-Typ '{request.Artifact}'." });
        }

        if (!ActionsEnabled())
        {
            return BadRequest(new { message = "Die BizTalk-Deaktivierung ist nicht aktiviert (features.bizTalk.actionsEnabled)." });
        }

        var server = _configService.GetServer(request.ServerId);
        if (server?.Checks?.BizTalk?.Enabled != true)
        {
            return BadRequest(new { message = $"Für Server '{request.ServerId}' ist die BizTalk-Überwachung nicht aktiviert." });
        }

        var (targetParent, error) = await ResolveTargetAsync(request.ServerId, artifact, request.TargetName);
        if (error != null)
            return BadRequest(new { message = error });

        var actionRequest = new BizTalkActionRequest
        {
            ServerId = request.ServerId,
            Artifact = artifact,
            TargetName = request.TargetName.Trim(),
            TargetParent = targetParent,
            RequestedBy = ResolveRequestedBy(request.RequestedBy),
            Comment = request.Comment
        };

        await _store.RequestAsync(actionRequest);

        return Accepted(new
        {
            id = actionRequest.Id,
            serverId = actionRequest.ServerId,
            artifact = actionRequest.Artifact.ToString(),
            targetName = actionRequest.TargetName,
            state = BizTalkActionState.Requested.ToString()
        });
    }

    /// <summary>
    /// Requests the deactivation of several artefacts at once (group deactivation). Every item is
    /// validated on its own; valid items are requested, invalid ones are reported back.
    /// </summary>
    [HttpPost("bulk")]
    public async Task<IActionResult> CreateBulk([FromBody] BulkBizTalkActionRequest request)
    {
        EnsureActionsDirectory();

        if (request.Items == null || request.Items.Count == 0)
            return BadRequest(new { message = "Keine Elemente übergeben." });

        if (!ActionsEnabled())
            return BadRequest(new { message = "Die BizTalk-Deaktivierung ist nicht aktiviert (features.bizTalk.actionsEnabled)." });

        var requested = new List<object>();
        var rejected = new List<object>();

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ServerId) ||
                string.IsNullOrWhiteSpace(item.Artifact) ||
                string.IsNullOrWhiteSpace(item.TargetName))
            {
                rejected.Add(new { serverId = item.ServerId, targetName = item.TargetName, reason = "Unvollständige Angaben." });
                continue;
            }

            if (!Enum.TryParse<BizTalkArtifactKind>(item.Artifact, ignoreCase: true, out var artifact))
            {
                rejected.Add(new { serverId = item.ServerId, targetName = item.TargetName, reason = $"Unbekannter Artefakt-Typ '{item.Artifact}'." });
                continue;
            }

            var server = _configService.GetServer(item.ServerId);
            if (server?.Checks?.BizTalk?.Enabled != true)
            {
                rejected.Add(new { serverId = item.ServerId, targetName = item.TargetName, reason = "BizTalk-Überwachung für diesen Server nicht aktiviert." });
                continue;
            }

            var (targetParent, error) = await ResolveTargetAsync(item.ServerId, artifact, item.TargetName);
            if (error != null)
            {
                rejected.Add(new { serverId = item.ServerId, targetName = item.TargetName, reason = error });
                continue;
            }

            var actionRequest = new BizTalkActionRequest
            {
                ServerId = item.ServerId,
                Artifact = artifact,
                TargetName = item.TargetName.Trim(),
                TargetParent = targetParent,
                RequestedBy = ResolveRequestedBy(request.RequestedBy),
                Comment = request.Comment
            };

            await _store.RequestAsync(actionRequest);
            requested.Add(new { id = actionRequest.Id, serverId = actionRequest.ServerId, targetName = actionRequest.TargetName });
        }

        return Ok(new { requested, rejected });
    }

    /// <summary>Whether the deactivation actions are enabled at all.</summary>
    [HttpGet("enabled")]
    public IActionResult GetEnabled()
        => Ok(new { enabled = ActionsEnabled() });

    private bool ActionsEnabled()
        => _configService.IsLoaded && _configService.GetFeatures().BizTalk.ActionsEnabled;

    /// <summary>
    /// Resolves the second path element the Management API needs: the owning application of an
    /// orchestration and the owning receive port of a receive location. Both are taken from the
    /// published status — never from the client.
    /// </summary>
    private async Task<(string? TargetParent, string? Error)> ResolveTargetAsync(
        string serverId,
        BizTalkArtifactKind artifact,
        string targetName)
    {
        if (artifact == BizTalkArtifactKind.Application || artifact == BizTalkArtifactKind.SendPort)
            return (null, null);

        var statusReader = HttpContext.RequestServices.GetService<StatusReaderService>();
        if (statusReader == null)
            return (null, "Der Status-Leser ist nicht verfügbar.");

        var server = await statusReader.GetServerAsync(serverId);
        var bizTalk = server?.BizTalk;
        if (bizTalk == null)
            return (null, $"Für Server '{serverId}' liegen keine BizTalk-Daten vor.");

        var artifactType = artifact == BizTalkArtifactKind.Orchestration ? "orchestration" : "receivelocation";
        var match = bizTalk.Problems
            .FirstOrDefault(a => a.ArtifactType == artifactType &&
                                  string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            // The artefact is not in the problem list any more (it was fixed meanwhile) — the
            // owning information is then taken from the checker's own view of the server.
            return (null, $"'{targetName}' wird aktuell nicht als Problem gemeldet — die Aktion kann nicht zugeordnet werden.");
        }

        var parent = artifact == BizTalkArtifactKind.Orchestration ? match.ApplicationName : match.ReceivePortName;
        if (string.IsNullOrWhiteSpace(parent))
            return (null, $"Für '{targetName}' ist kein übergeordnetes Element bekannt.");

        return (parent, null);
    }

    /// <summary>Prefer the authenticated Windows user over the client-supplied name.</summary>
    private string ResolveRequestedBy(string? clientSupplied)
        => User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
            ? User.Identity.Name!
            : (string.IsNullOrWhiteSpace(clientSupplied) ? "WebUI" : clientSupplied);
}
