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
}

/// <summary>Default thresholds for BizTalk monitoring.</summary>
public class BizTalkDefaults
{
    /// <summary>Suspended-instance count at/above which a Warning is raised (default 1).</summary>
    public int SuspendedWarning { get; set; } = 1;

    /// <summary>Suspended-instance count at/above which a Critical is raised (default 25).</summary>
    public int SuspendedCritical { get; set; } = 25;
}
