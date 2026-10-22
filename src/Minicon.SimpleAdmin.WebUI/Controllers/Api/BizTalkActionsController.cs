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
    private readonly StatusReaderService _statusReader;
    private readonly ILogger<BizTalkActionsController> _logger;

    public BizTalkActionsController(
        BizTalkActionStore store,
        ConfigurationService configService,
        StatusReaderService statusReader,
        ILogger<BizTalkActionsController> logger)
    {
        _store = store;
        _configService = configService;
        _statusReader = statusReader;
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
    [ValidateAntiForgeryToken]
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

        var (targetParent, canonicalName, error) = await ResolveTargetAsync(request.ServerId, artifact, request.TargetName);
        if (error != null)
            return BadRequest(new { message = error });

        var actionRequest = new BizTalkActionRequest
        {
            ServerId = request.ServerId,
            Artifact = artifact,
            TargetName = canonicalName,
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
    [ValidateAntiForgeryToken]
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

            var (targetParent, canonicalName, error) = await ResolveTargetAsync(item.ServerId, artifact, item.TargetName);
            if (error != null)
            {
                rejected.Add(new { serverId = item.ServerId, targetName = item.TargetName, reason = error });
                continue;
            }

            var actionRequest = new BizTalkActionRequest
            {
                ServerId = item.ServerId,
                Artifact = artifact,
                TargetName = canonicalName,
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
    /// Resolves the artefact in the published status and derives everything the Management API
    /// needs from there: the canonical name and the owning application / receive port.
    /// <para>
    /// All four artefact types are validated against the published status. A client can therefore
    /// not name an arbitrary running application or send port — only an element that the server
    /// actually reports as a problem can be deactivated.
    /// </para>
    /// </summary>
    private async Task<(string? TargetParent, string CanonicalName, string? Error)> ResolveTargetAsync(
        string serverId,
        BizTalkArtifactKind artifact,
        string targetName)
    {
        var statusReader = _statusReader;
        var server = await statusReader.GetServerAsync(serverId);
        var bizTalk = server?.BizTalk;
        if (bizTalk == null)
            return (null, string.Empty, $"Für Server '{serverId}' liegen keine BizTalk-Daten vor.");

        var artifactType = ArtifactTypeOf(artifact);
        var match = bizTalk.Problems
            .FirstOrDefault(a => string.Equals(a.ArtifactType, artifactType, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(a.Name, targetName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            // The element is not in the problem list (any more) — the action cannot be assigned
            // unambiguously, so it is refused.
            return (null, string.Empty,
                $"'{targetName.Trim()}' wird für Server '{serverId}' aktuell nicht als {LabelOf(artifact)} gemeldet — die Aktion kann nicht zugeordnet werden.");
        }

        var canonicalName = match.Name;

        // Applications and send ports have no parent; the API expects only their name.
        if (artifact == BizTalkArtifactKind.Application || artifact == BizTalkArtifactKind.SendPort)
            return (null, canonicalName, null);

        var parent = artifact == BizTalkArtifactKind.Orchestration ? match.ApplicationName : match.ReceivePortName;
        if (string.IsNullOrWhiteSpace(parent))
            return (null, string.Empty, $"Für '{canonicalName}' ist kein übergeordnetes Element bekannt.");

        return (parent, canonicalName, null);
    }

    /// <summary>The artefact type as the checker publishes it in the problem list.</summary>
    private static string ArtifactTypeOf(BizTalkArtifactKind artifact) => artifact switch
    {
        BizTalkArtifactKind.Application => "application",
        BizTalkArtifactKind.Orchestration => "orchestration",
        BizTalkArtifactKind.SendPort => "sendport",
        BizTalkArtifactKind.ReceiveLocation => "receivelocation",
        _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, "Unbekannter Artefakt-Typ.")
    };

    /// <summary>Readable name of the artefact type (for the error message).</summary>
    private static string LabelOf(BizTalkArtifactKind artifact) => artifact switch
    {
        BizTalkArtifactKind.Application => "Anwendung",
        BizTalkArtifactKind.Orchestration => "Orchestrierung",
        BizTalkArtifactKind.SendPort => "Sendeport",
        BizTalkArtifactKind.ReceiveLocation => "Empfangsort",
        _ => "BizTalk-Element"
    };

    /// <summary>Prefer the authenticated Windows user over the client-supplied name.</summary>
    private string ResolveRequestedBy(string? clientSupplied)
        => User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
            ? User.Identity.Name!
            : (string.IsNullOrWhiteSpace(clientSupplied) ? "WebUI" : clientSupplied);
}
