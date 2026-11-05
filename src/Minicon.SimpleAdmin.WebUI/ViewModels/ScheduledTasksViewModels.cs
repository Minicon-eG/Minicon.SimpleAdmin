using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Helpers;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using static Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator;

namespace Minicon.SimpleAdmin.WebUI.ViewModels;

/// <summary>Scheduled task page (<c>/ScheduledTasks</c>): one task × server matrix per environment.</summary>
public sealed class ScheduledTasksPageViewModel
{
    public bool FeatureEnabled { get; init; }
    public bool ControlEnabled { get; init; }
    public string? DisabledReason { get; init; }
    public List<ScheduledTaskEnvironmentView> Environments { get; init; } = new();
    public IReadOnlyList<ScheduledTaskAuditEntry> Audit { get; init; } = Array.Empty<ScheduledTaskAuditEntry>();

    /// <summary>Last worker update per server ID (for "Stand Worker").</summary>
    public Dictionary<string, DateTime> WorkerUpdates { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int TaskCount => Environments.Sum(e => e.Rows.Count);
    public int ActiveCount => Environments.Sum(e => e.Rows.Count(r => r.Group.ActiveServers.Count > 0));
    public int ErrorCount => Environments.Sum(e => e.Rows.Count(r => r.HasFinding));
    public int ViolationCount => Environments.Sum(e => e.Rows.Count(r => r.IsViolation));

    public static ScheduledTasksPageViewModel Build(
        bool featureEnabled,
        IReadOnlyList<TaskGroup> groups,
        IReadOnlyDictionary<string, DateTime> workerUpdates,
        bool controlEnabled,
        string? disabledReason,
        IReadOnlyList<ScheduledTaskAuditEntry> audit)
    {
        var environments = groups
            .GroupBy(g => g.Environment, StringComparer.OrdinalIgnoreCase)
            .Select(env => new ScheduledTaskEnvironmentView
            {
                Name = env.Key,
                Servers = env.SelectMany(g => g.Members.Select(m => m.ServerId).Concat(g.UnknownServers))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Rows = env.Select(g => new ScheduledTaskRowView(g)).ToList()
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScheduledTasksPageViewModel
        {
            FeatureEnabled = featureEnabled,
            ControlEnabled = controlEnabled,
            DisabledReason = controlEnabled ? null : disabledReason,
            Environments = environments,
            Audit = audit,
            WorkerUpdates = new Dictionary<string, DateTime>(workerUpdates, StringComparer.OrdinalIgnoreCase)
        };
    }
}

/// <summary>Matrix of one environment.</summary>
public sealed class ScheduledTaskEnvironmentView
{
    /// <summary>Environment key ("" = servers without environment).</summary>
    public string Name { get; init; } = string.Empty;

    public string Label => string.IsNullOrEmpty(Name) ? "Ohne Umgebung" : Name;

    /// <summary>Server columns: every server of the environment that reported a task or is unknown.</summary>
    public List<string> Servers { get; init; } = new();

    public List<ScheduledTaskRowView> Rows { get; init; } = new();
}

/// <summary>One task path (row) of an environment matrix.</summary>
public sealed class ScheduledTaskRowView
{
    public ScheduledTaskRowView(TaskGroup group)
    {
        Group = group;
        Violation = ScheduledTaskTargets.Violation(group);
    }

    public TaskGroup Group { get; }
    public ScheduledTaskViolation Violation { get; }

    public string Path => Group.TaskPath;
    public string Name => Group.Members.FirstOrDefault()?.Task.Name is { Length: > 0 } name
        ? name
        : Path.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Path;
    public string? Description => Group.Members.Select(m => m.Task.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

    public bool IsViolation => Violation is ScheduledTaskViolation.MultipleActive or ScheduledTaskViolation.NoneActive;
    public bool HasFinding => Group.Members.Any(m => ScheduledTaskDisplay.HasFinding(m.Task));
    public bool HasProblem => IsViolation || HasFinding;

    public GroupMember? Member(string serverId) =>
        Group.Members.FirstOrDefault(m => string.Equals(m.ServerId, serverId, StringComparison.OrdinalIgnoreCase));

    public bool IsUnknown(string serverId) =>
        Group.UnknownServers.Contains(serverId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Servers that can be chosen for "Hier exklusiv aktiv" (members + unknown).</summary>
    public IEnumerable<string> CandidateServers =>
        Group.Members.Select(m => m.ServerId).Concat(Group.UnknownServers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    public string ViolationText => Violation switch
    {
        ScheduledTaskViolation.MultipleActive => $"Auf {Group.ActiveServers.Count} Servern aktiv ({string.Join(", ", Group.ActiveServers)}) – erlaubt ist genau einer",
        ScheduledTaskViolation.NoneActive => "Auf keinem Server aktiv – die Task läuft nirgends",
        ScheduledTaskViolation.PossiblyNoneActive => $"Auf keinem bekannten Server aktiv – Zustand unbekannt auf {string.Join(", ", Group.UnknownServers)}",
        _ => string.Empty
    };
}

// ── Servers/Edit: "Geplante Tasks" card ──────────────────────────────────────

public class ScheduledTaskChecksViewModel
{
    public bool Enabled { get; set; }
    public bool CheckEvents { get; set; } = true;
    public List<ScheduledTaskWatchRowViewModel> Tasks { get; set; } = new();

    /// <summary>
    /// Null when the card is off and has no rows. Rows without a pattern are dropped. When switched off with rows,
    /// the rows are kept (<c>enabled = false</c>) so turning the check off does not lose the configuration.
    /// </summary>
    public ServerScheduledTasksConfig? ToServerScheduledTasksConfig()
    {
        var tasks = Tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
            .Select(t => t.ToWatchConfig())
            .ToList();

        if (!Enabled && tasks.Count == 0)
            return null;

        return new ServerScheduledTasksConfig { Enabled = Enabled, CheckEvents = CheckEvents, Tasks = tasks };
    }

    public static ScheduledTaskChecksViewModel FromServerScheduledTasksConfig(ServerScheduledTasksConfig? config)
    {
        if (config == null)
            return new ScheduledTaskChecksViewModel();

        return new ScheduledTaskChecksViewModel
        {
            Enabled = config.Enabled,
            CheckEvents = config.CheckEvents,
            Tasks = config.Tasks.Select(ScheduledTaskWatchRowViewModel.FromWatchConfig).ToList()
        };
    }
}

public class ScheduledTaskWatchRowViewModel
{
    public string Pattern { get; set; } = string.Empty;
    public string? ExcludePatterns { get; set; }       // comma-separated
    public ScheduledTaskMode Mode { get; set; } = ScheduledTaskMode.SingleActive;
    public int? MaxRuntimeMinutes { get; set; }
    public int? MissedRunGraceMinutes { get; set; }
    public bool CheckMissedRun { get; set; } = true;
    public string? AllowedResultCodes { get; set; }    // comma-separated, decimal or 0x hex
    public bool FailureIsCritical { get; set; } = true;
    public string? WikiUrl { get; set; }
    public string? RemediationSteps { get; set; }      // newline-separated

    public ScheduledTaskWatchConfig ToWatchConfig()
    {
        var config = new ScheduledTaskWatchConfig
        {
            Pattern = (Pattern ?? string.Empty).Trim(),
            Mode = Mode,
            MaxRuntimeMinutes = MaxRuntimeMinutes,
            MissedRunGraceMinutes = MissedRunGraceMinutes,
            CheckMissedRun = CheckMissedRun,
            FailureIsCritical = FailureIsCritical,
            WikiUrl = string.IsNullOrWhiteSpace(WikiUrl) ? null : WikiUrl.Trim(),
            RemediationSteps = FileMonitoringChecksViewModel.SplitLines(RemediationSteps)
        };
        config.ExcludePatternsText = ExcludePatterns ?? string.Empty;
        config.AllowedResultCodesText = AllowedResultCodes ?? string.Empty;
        return config;
    }

    public static ScheduledTaskWatchRowViewModel FromWatchConfig(ScheduledTaskWatchConfig c) => new()
    {
        Pattern = c.Pattern,
        ExcludePatterns = c.ExcludePatternsText,
        Mode = c.Mode,
        MaxRuntimeMinutes = c.MaxRuntimeMinutes,
        MissedRunGraceMinutes = c.MissedRunGraceMinutes,
        CheckMissedRun = c.CheckMissedRun,
        AllowedResultCodes = c.AllowedResultCodesText,
        FailureIsCritical = c.FailureIsCritical,
        WikiUrl = c.WikiUrl,
        RemediationSteps = string.Join("\n", c.RemediationSteps)
    };
}
