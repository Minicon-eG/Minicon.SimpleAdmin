namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// The kind of BizTalk artefact an action addresses. Mirrors the artefact types used by
/// <see cref="BizTalkArtifact.ArtifactType"/> ("application", "orchestration", "sendport",
/// "receivelocation") but as a typed enum so the API contract stays stable.
/// </summary>
public enum BizTalkArtifactKind
{
    /// <summary>A BizTalk application (stop).</summary>
    Application,

    /// <summary>An orchestration (stop).</summary>
    Orchestration,

    /// <summary>A send port (stop).</summary>
    SendPort,

    /// <summary>A receive location (disable).</summary>
    ReceiveLocation
}

/// <summary>
/// Lifecycle state of a requested BizTalk action. The request is written by the WebUI,
/// the worker on the target server executes it and reports the outcome.
/// </summary>
public enum BizTalkActionState
{
    /// <summary>The request file was written; the worker has not picked it up yet.</summary>
    Requested,

    /// <summary>The worker is executing the action.</summary>
    Running,

    /// <summary>The action was executed successfully.</summary>
    Succeeded,

    /// <summary>The action failed (API error, artefact not found, …).</summary>
    Failed
}

/// <summary>
/// A request to deactivate a single BizTalk artefact. Written by the WebUI into the shared
/// action directory; picked up by the worker on the target server.
/// </summary>
public class BizTalkActionRequest
{
    /// <summary>Unique request id (also the file name).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Id of the server the artefact lives on (worker matches on its own server id).</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Which artefact kind to address.</summary>
    public BizTalkArtifactKind Artifact { get; set; }

    /// <summary>
    /// The name the Management API expects in the URL path: application name, orchestration
    /// name, send port name or receive location name.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Second path element where the API needs one: the application of an orchestration or the
    /// receive port of a receive location. Null for applications and send ports.
    /// </summary>
    public string? TargetParent { get; set; }

    /// <summary>Who requested the action (WebUI user).</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Optional free-text reason.</summary>
    public string? Comment { get; set; }

    /// <summary>When the WebUI created the request (UTC).</summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The outcome of a requested BizTalk action, written by the worker on the target server.
/// </summary>
public class BizTalkActionResult
{
    /// <summary>Id of the request this result belongs to.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Id of the server the action was executed on.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Terminal state of the action.</summary>
    public BizTalkActionState State { get; set; }

    /// <summary>Human-readable summary (e.g. "Anwendung 'X' gestoppt").</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Error detail when <see cref="State"/> is <see cref="BizTalkActionState.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>When the worker finished the action (UTC).</summary>
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A request together with its (possibly still missing) result — the view the WebUI shows.
/// </summary>
public class BizTalkActionView
{
    /// <summary>The request.</summary>
    public BizTalkActionRequest Request { get; set; } = new();

    /// <summary>The result, or null while the worker has not reported back yet.</summary>
    public BizTalkActionResult? Result { get; set; }

    /// <summary>Effective state: the result's state, or <see cref="BizTalkActionState.Requested"/> while pending.</summary>
    public BizTalkActionState State => Result?.State ?? BizTalkActionState.Requested;
}
