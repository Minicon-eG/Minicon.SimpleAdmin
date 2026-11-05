using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.WebUI.ViewModels;

/// <summary>
/// Aggregated view of a server's status from all service RuntimeStatus files
/// </summary>
public class ServerStatusView
{
    public string ServerId { get; set; } = string.Empty;
    public DateTime LastCheck { get; set; }
    public ServiceStatus OverallStatus { get; set; }
    public List<Metric> Metrics { get; set; } = new();
    public Dictionary<string, AppPoolState>? AppPools { get; set; }
    public Dictionary<string, ServiceState>? WindowsServices { get; set; }
    public EventLogState? EventLogs { get; set; }
    public Dictionary<string, SqlQueryCheckState> SqlQueryChecks { get; set; } = new();
    public Dictionary<string, EmailProbeState>? EmailProbes { get; set; }
    public Dictionary<string, EmailDeliveryCheckState>? EmailDelivery { get; set; }
    public Dictionary<string, CertificateState>? Certificates { get; set; }
    public BizTalkState? BizTalk { get; set; }
    public FileMonitoringState? FileMonitoring { get; set; }
    public ScheduledTaskState? ScheduledTasks { get; set; }

    /// <summary>
    /// True when the server has no status at all and is only shown because it is the anchor of scheduled task
    /// group problems (anchor = first participant of the environment from config.json).
    /// </summary>
    public bool IsTaskGroupAnchorPlaceholder { get; set; }
    public List<ActiveProblem> ActiveProblems { get; set; } = new();
    public List<RuntimeStatus> Services { get; set; } = new();
    public string? BaseUrl { get; set; }
}
