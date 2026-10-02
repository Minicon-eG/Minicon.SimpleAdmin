namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Snapshot of BizTalk health for one server, gathered from the BizTalk Management API.
/// Stores only the problematic artefacts plus per-category totals to keep the payload small
/// (this object is serialized into the per-server status JSON published over HTTP).
/// </summary>
public class BizTalkState
{
    /// <summary>False when the Management API could not be reached.</summary>
    public bool ApiReachable { get; set; } = true;

    /// <summary>Error detail when the API was unreachable.</summary>
    public string? ApiError { get; set; }

    public int ApplicationsTotal { get; set; }
    public int OrchestrationsTotal { get; set; }
    public int SendPortsTotal { get; set; }
    public int ReceiveLocationsTotal { get; set; }

    /// <summary>The artefacts that are not in their expected state.</summary>
    public List<BizTalkArtifact> Problems { get; set; } = new();

    /// <summary>Total number of suspended service instances.</summary>
    public int SuspendedTotal { get; set; }

    /// <summary>Suspended-instance counts grouped by application (when enabled).</summary>
    public List<BizTalkSuspendedGroup> SuspendedGroups { get; set; } = new();

    /// <summary>Status derived from the suspended-instance count vs the configured thresholds.</summary>
    public MetricStatus SuspendedStatus { get; set; } = MetricStatus.Healthy;
}

/// <summary>A BizTalk artefact that is not in its expected state.</summary>
public class BizTalkArtifact
{
    /// <summary>application | orchestration | sendport | receivelocation</summary>
    public string ArtifactType { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Raw status reported by BizTalk (e.g. "Stopped", "Bound", "Disabled").</summary>
    public string Status { get; set; } = string.Empty;

    public bool IsCritical { get; set; } = true;
}

/// <summary>Suspended-instance count for one scope (application name).</summary>
public class BizTalkSuspendedGroup
{
    public string Scope { get; set; } = string.Empty;
    public int Count { get; set; }
}
