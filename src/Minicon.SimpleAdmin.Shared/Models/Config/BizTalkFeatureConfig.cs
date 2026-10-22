namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Global BizTalk monitoring feature configuration (opt-in). The API endpoint and which artefacts
/// to watch are configured per server in <see cref="ServerBizTalkConfig"/>.
/// </summary>
public class BizTalkFeatureConfig
{
    /// <summary>Whether the feature is enabled globally (default: false - opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Optional: custom check interval in seconds (null = use global interval).</summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>Default thresholds for BizTalk monitoring.</summary>
    public BizTalkDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Master toggle for the BizTalk deactivation actions (stop application / orchestration /
    /// send port, disable receive location). When disabled, the WebUI shows no action buttons and
    /// the worker ignores pending requests (default: false - opt-in).
    /// </summary>
    public bool ActionsEnabled { get; set; } = false;

    /// <summary>
    /// Directory in which the WebUI and the workers exchange BizTalk action requests and results.
    /// Must be reachable by the central WebUI and by every worker that may execute an action —
    /// typically a UNC share (e.g. "\\\\fileserver\\biztalk-actions").
    /// Empty = the local status directory of the respective process (single-host setup).
    /// </summary>
    public string ActionsDirectory { get; set; } = string.Empty;
}

/// <summary>Default thresholds for BizTalk monitoring.</summary>
public class BizTalkDefaults
{
    /// <summary>Suspended-instance count at/above which a Warning is raised (default 1).</summary>
    public int SuspendedWarning { get; set; } = 1;

    /// <summary>Suspended-instance count at/above which a Critical is raised (default 25).</summary>
    public int SuspendedCritical { get; set; } = 25;
}
