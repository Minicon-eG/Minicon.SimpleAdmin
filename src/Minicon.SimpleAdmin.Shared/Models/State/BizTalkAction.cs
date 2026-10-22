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
    Failed,

    /// <summary>
    /// The outcome is unclear: the worker claimed the request and then stopped without reporting
    /// a result. The action may or may not have been executed, so it is never repeated
    /// automatically — a person has to check the target.
    /// </summary>
    Uncertain
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
/// The exclusive claim a worker takes on a request before it executes it. The claim file is
/// created exclusively (create-new), so only one worker can ever own a request — even across
/// processes and machines sharing the directory. A request with a claim is never executed a
/// second time, because the outcome of an interrupted execution cannot be reconstructed.
/// </summary>
public class BizTalkActionClaim
{
    /// <summary>Id of the claimed request.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Id of the server the request was claimed for.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Which worker process holds the claim.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>When the claim was taken (UTC).</summary>
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the worker could not persist the result after the action was carried out. The
    /// action may have been executed, so it is never repeated automatically.
    /// </summary>
    public bool OutcomeUncertain { get; set; }
}

/// <summary>
/// A request together with its (possibly still missing) result — the view the WebUI shows.
/// </summary>
public class BizTalkActionView
{
    /// <summary>
    /// A claim older than this is treated as abandoned: the worker most likely died during the
    /// execution, so the outcome is unclear and a person has to check the target.
    /// </summary>
    public static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>The request.</summary>
    public BizTalkActionRequest Request { get; set; } = new();

    /// <summary>The result, or null while the worker has not reported back yet.</summary>
    public BizTalkActionResult? Result { get; set; }

    /// <summary>The claim of the worker that took the request, or null while it is unclaimed.</summary>
    public BizTalkActionClaim? Claim { get; set; }

    /// <summary>
    /// Effective state of the action: the reported result wins; a claim without a result means
    /// the action is running — or was abandoned, which leaves the outcome unclear.
    /// </summary>
    public BizTalkActionState State
    {
        get
        {
            if (Result != null)
                return Result.State;

            if (Claim == null)
                return BizTalkActionState.Requested;

            return Claim.OutcomeUncertain || DateTime.UtcNow - Claim.ClaimedAt > ClaimStaleAfter
                ? BizTalkActionState.Uncertain
                : BizTalkActionState.Running;
        }
    }
}
