using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using static Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// REST endpoints of the scheduled task page: task groups with status, live state, control actions
/// (enable/disable/run/end, "activate exclusively") and the audit log. Targets are validated against
/// config.json and the reported task groups — servers and paths never come from the client alone, and
/// the group servers of "activate exclusively" are determined here. All POSTs require a JSON body
/// (<c>[Consumes("application/json")]</c>) so they cannot be triggered by a cross-site form post riding
/// on the user's Windows credentials.
/// </summary>
[ApiController]
[Route("api/scheduledtasks")]
public sealed class ScheduledTasksController : ControllerBase
{
    private readonly IScheduledTaskControl _control;
    private readonly ConfigurationService _configService;
    private readonly Func<Task<IReadOnlyList<TaskGroup>>> _groups;

    public ScheduledTasksController(
        IScheduledTaskControl control,
        ConfigurationService configService,
        StatusReaderService statusReader)
        : this(control, configService, () => statusReader.GetScheduledTaskGroupsAsync())
    {
    }

    /// <summary>Test seam: groups from a delegate instead of the status reader.</summary>
    internal ScheduledTasksController(
        IScheduledTaskControl control,
        ConfigurationService configService,
        Func<Task<IReadOnlyList<TaskGroup>>> groups)
    {
        _control = control;
        _configService = configService;
        _groups = groups;
    }

    public sealed class ActionRequest
    {
        public string? Server { get; set; }
        public string? Path { get; set; }
        public ScheduledTaskAction? Action { get; set; }
    }

    public sealed class ExclusiveRequest
    {
        public string? Path { get; set; }
        public string? TargetServer { get; set; }
    }

    /// <summary>Task groups of all environments incl. the single-active finding.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var featureEnabled = _configService.IsLoaded && _configService.GetFeatures().ScheduledTasks.Enabled;
        var groups = featureEnabled ? await _groups() : Array.Empty<TaskGroup>();
        return Ok(new
        {
            featureEnabled,
            controlEnabled = _control.IsEnabled,
            disabledReason = _control.IsEnabled ? null : _control.DisabledReason,
            groups = groups.Select(g => new
            {
                environment = g.Environment,
                taskPath = g.TaskPath,
                mode = g.Mode.ToString(),
                anchorServer = g.AnchorServer,
                activeServers = g.ActiveServers,
                unknownServers = g.UnknownServers,
                violation = ScheduledTaskTargets.Violation(g).ToString(),
                members = g.Members.Select(m => new
                {
                    serverId = m.ServerId,
                    enabled = m.Enabled,
                    state = m.State,
                    task = m.Task
                })
            })
        });
    }

    /// <summary>Current state of one task on one server, queried directly (independent of the worker cycle).</summary>
    [HttpGet("live")]
    public async Task<IActionResult> Live([FromQuery] string? server, [FromQuery] string? path, CancellationToken ct)
    {
        if (Disabled() is { } disabled) return disabled;
        var (target, error) = await ResolveAsync(server, path);
        if (error != null) return error;

        var state = await _control.QueryAsync(target!.Value.Server, target.Value.Path, ct);
        return Ok(state);
    }

    /// <summary>Enable, disable, run or end a task on one server.</summary>
    [HttpPost("action")]
    [Consumes("application/json")]
    public async Task<IActionResult> Execute([FromBody] ActionRequest request, CancellationToken ct)
    {
        if (Disabled() is { } disabled) return disabled;
        if (request.Action is not { } action || !Enum.IsDefined(action))
            return BadRequest(new { message = "Unbekannte Aktion." });
        var (target, error) = await ResolveAsync(request.Server, request.Path);
        if (error != null) return error;

        var result = await _control.ExecuteAsync(target!.Value.Server, target.Value.Path, action, CurrentUser(), ct);
        return Ok(result);
    }

    /// <summary>
    /// Makes the target the only server with the task enabled. The group servers (members + unknown servers of
    /// the environment) are determined from the status data, not taken from the client.
    /// </summary>
    [HttpPost("activate-exclusive")]
    [Consumes("application/json")]
    public async Task<IActionResult> ActivateExclusive([FromBody] ExclusiveRequest request, CancellationToken ct)
    {
        if (Disabled() is { } disabled) return disabled;
        var (target, error) = await ResolveAsync(request.TargetServer, request.Path);
        if (error != null) return error;

        // A group server that no longer resolves to an active config server with task monitoring (deactivated,
        // check switched off) would be skipped and could keep the task enabled → refuse instead of claiming exclusivity.
        var unresolved = ScheduledTaskTargets.UnresolvedServers(_configService.Config, target!.Value.Group);
        if (unresolved.Count > 0)
            return Conflict(new
            {
                message = $"Exklusiv-Schaltung abgelehnt: {string.Join(", ", unresolved)} meldet die Task noch, ist in der Konfiguration "
                          + "aber nicht (mehr) aktiv bzw. ohne Task-Überwachung und kann deshalb nicht deaktiviert werden. "
                          + "Bitte die Task dort direkt deaktivieren oder den Server wieder für die Überwachung aktivieren.",
                servers = unresolved
            });

        var groupServers = ScheduledTaskTargets.GroupServerNames(_configService.Config, target.Value.Group);
        if (!groupServers.Contains(target.Value.Server, StringComparer.OrdinalIgnoreCase))
            groupServers.Add(target.Value.Server);

        // Servers whose worker reported the task: not finding it there is an error for the control service
        // (e.g. invisible to the WebUI account), so the target is not enabled while it may still run elsewhere.
        var reporting = ScheduledTaskTargets.ReportingServerNames(_configService.Config, target.Value.Group);

        var result = await _control.ActivateExclusivelyAsync(
            target.Value.Path, target.Value.Server, groupServers, CurrentUser(), serversReportingTask: reporting, ct: ct);
        return Ok(result);
    }

    /// <summary>Audit log of the control actions (newest first), optionally for one task path.</summary>
    [HttpGet("audit")]
    public IActionResult Audit([FromQuery] string? path, [FromQuery] int max = 200)
        => Ok(_control.GetAuditLog(Math.Clamp(max, 1, 1000), string.IsNullOrWhiteSpace(path) ? null : path.Trim()));

    private IActionResult? Disabled()
    {
        if (_control.IsEnabled)
            return null;
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            message = _control.DisabledReason ?? "Die Steuerung geplanter Tasks ist auf diesem Server abgeschaltet."
        });
    }

    /// <summary>
    /// Resolves server + path to a config server name and the canonical task path of its group.
    /// Missing input → 400, unknown server or path → 404.
    /// </summary>
    private async Task<((string Server, string Path, TaskGroup Group)? Target, IActionResult? Error)> ResolveAsync(
        string? serverRef, string? path)
    {
        if (string.IsNullOrWhiteSpace(serverRef) || string.IsNullOrWhiteSpace(path))
            return (null, BadRequest(new { message = "Server und Task-Pfad sind erforderlich." }));
        if (!_configService.IsLoaded || !_configService.GetFeatures().ScheduledTasks.Enabled)
            return (null, BadRequest(new { message = "Die Überwachung geplanter Tasks ist nicht aktiviert." }));

        var server = ScheduledTaskTargets.ResolveServer(_configService.Config, serverRef);
        if (server == null)
            return (null, NotFound(new { message = $"Server '{serverRef}' ist nicht für die Überwachung geplanter Tasks konfiguriert." }));

        var group = ScheduledTaskTargets.FindGroup(await _groups(), server, path);
        if (group == null)
            return (null, NotFound(new { message = $"Task '{path}' ist für Server '{server.Name}' nicht bekannt." }));

        return ((server.Name, group.TaskPath, group), null);
    }

    private string CurrentUser() =>
        User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
            ? User.Identity.Name!
            : "WebUI";
}
