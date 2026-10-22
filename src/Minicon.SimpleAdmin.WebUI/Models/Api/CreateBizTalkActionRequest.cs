namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Request to deactivate a single BizTalk artefact from the problems list.
/// </summary>
public class CreateBizTalkActionRequest
{
    /// <summary>Id of the server the artefact lives on.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Artefact type: "application", "orchestration", "sendport" or "receivelocation".
    /// </summary>
    public string Artifact { get; set; } = string.Empty;

    /// <summary>The name of the artefact (as shown in the problem message).</summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Who requests the action (falls back to the authenticated Windows user).</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Optional reason for the deactivation.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Request to deactivate several BizTalk artefacts at once (group deactivation).
/// </summary>
public class BulkBizTalkActionRequest
{
    /// <summary>The artefacts to deactivate.</summary>
    public List<CreateBizTalkActionRequest> Items { get; set; } = new();

    /// <summary>Who requests the action (falls back to the authenticated Windows user).</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Optional shared reason for the deactivation.</summary>
    public string? Comment { get; set; }
}
