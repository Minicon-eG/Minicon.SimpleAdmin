namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Per-server BizTalk monitoring configuration. Talks to the BizTalk Management Service REST API
/// (typically on the BizTalk server itself, via integrated Windows authentication).
/// </summary>
public class ServerBizTalkConfig
{
    /// <summary>Whether BizTalk checks are enabled for this server (default: false).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the BizTalk Management Service (default: localhost on the BizTalk server).</summary>
    public string BaseUrl { get; set; } = "http://localhost/BizTalkManagementService";

    /// <summary>Optional HTTP timeout in seconds (default 30).</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Application monitoring (expected status: Started).</summary>
    public BizTalkArtifactMonitor Applications { get; set; } = new();

    /// <summary>Orchestration monitoring (expected status: Started).</summary>
    public BizTalkArtifactMonitor Orchestrations { get; set; } = new();

    /// <summary>Send port monitoring (expected status: Started).</summary>
    public BizTalkArtifactMonitor SendPorts { get; set; } = new();

    /// <summary>Receive location monitoring (disabled = problem).</summary>
    public BizTalkReceiveLocationMonitor ReceiveLocations { get; set; } = new();

    /// <summary>Suspended-instance monitoring.</summary>
    public BizTalkSuspendedConfig SuspendedInstances { get; set; } = new();
}

/// <summary>Selects which artefacts of a kind to monitor.</summary>
public class BizTalkArtifactMonitor
{
    /// <summary>"all" (default), "list" (only <see cref="Include"/>), or "none".</summary>
    public string Monitor { get; set; } = "all";

    /// <summary>When Monitor = "list": only these names are checked.</summary>
    public List<string> Include { get; set; } = new();

    /// <summary>When Monitor = "all": these names are skipped (e.g. system artefacts).</summary>
    public List<string> Exclude { get; set; } = new();
}

/// <summary>Receive location monitoring with configurable severity for disabled locations.</summary>
public class BizTalkReceiveLocationMonitor : BizTalkArtifactMonitor
{
    /// <summary>Severity for a disabled receive location: "Warning" (default) or "Critical".</summary>
    public string DisabledSeverity { get; set; } = "Warning";

    /// <summary>Receive locations that are always Critical when disabled (overrides DisabledSeverity).</summary>
    public List<string> CriticalList { get; set; } = new();
}

/// <summary>Suspended-instance monitoring configuration.</summary>
public class BizTalkSuspendedConfig
{
    /// <summary>Whether suspended-instance monitoring is enabled (default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Override of the global Warning threshold (null = use feature default).</summary>
    public int? Warning { get; set; }

    /// <summary>Override of the global Critical threshold (null = use feature default).</summary>
    public int? Critical { get; set; }

    /// <summary>Group suspended counts by application (for the mail/detail breakdown).</summary>
    public bool ByApplication { get; set; } = true;
}
