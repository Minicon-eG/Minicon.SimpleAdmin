using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using StatusMetricStatus = Minicon.SimpleAdmin.Models.Status.MetricStatus;
using StateMetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Derives active problems deterministically from metrics and AppPool states
/// </summary>
public static class ProblemDerivationService
{
    /// <summary>
    /// Derives a list of active problems from current metrics and all checker states
    /// </summary>
    public static List<ActiveProblem> DeriveProblems(
        List<Metric> metrics,
        Dictionary<string, AppPoolState>? appPools,
        Dictionary<string, ServiceState>? services = null,
        EventLogState? eventLogs = null,
        Dictionary<string, SqlQueryCheckState>? sqlQueryChecks = null,
        Dictionary<string, EmailProbeState>? emailProbes = null,
        Dictionary<string, EmailDeliveryCheckState>? emailDelivery = null,
        Dictionary<string, CertificateState>? certificates = null,
        BizTalkState? bizTalk = null,
        FileMonitoringState? fileMonitoring = null)
    {
        var problems = new List<ActiveProblem>();

        // Derive problems from metrics
        foreach (var metric in metrics)
        {
            if (metric.Status == StatusMetricStatus.Critical || metric.Status == StatusMetricStatus.Warning)
            {
                var isCritical = metric.Status == StatusMetricStatus.Critical;
                var threshold = isCritical
                    ? metric.Threshold?.Critical ?? 0
                    : metric.Threshold?.Warning ?? 0;

                var problem = metric.Name.ToLowerInvariant() switch
                {
                    "cpu" => ActiveProblem.CpuHigh(metric.Value, threshold, isCritical),
                    "memory" => ActiveProblem.MemoryHigh(metric.Value, threshold, isCritical),
                    "disk" => ActiveProblem.DiskHigh(metric.Target ?? "C:", metric.Value, threshold, isCritical),
                    _ => null
                };

                if (problem != null)
                {
                    problems.Add(problem);
                }
            }
        }

        // Derive problems from AppPools
        if (appPools != null)
        {
            foreach (var (name, pool) in appPools)
            {
                if (pool.Status == AppPoolStatus.Stopped)
                {
                    problems.Add(ActiveProblem.AppPoolStopped(name));
                }

                if (pool.MemoryStatus is StateMetricStatus.Critical or StateMetricStatus.Warning)
                {
                    var isCritical = pool.MemoryStatus == StateMetricStatus.Critical;
                    var threshold = isCritical ? pool.MemoryCriticalMB : pool.MemoryWarningMB;
                    problems.Add(ActiveProblem.AppPoolHighMemory(name, pool.MemoryMB, threshold, isCritical));
                }

                if (pool.UptimeStatus == StateMetricStatus.Warning)
                {
                    problems.Add(ActiveProblem.AppPoolLongUptime(name, pool.UptimeHours, pool.UptimeWarningHours));
                }
            }
        }

        // Derive problems from Windows Services
        if (services != null)
        {
            foreach (var (name, svc) in services)
            {
                if (!svc.IsHealthy)
                {
                    if (svc.Status == "PatternNoMatch")
                        problems.Add(ActiveProblem.ServicePatternNoMatch(name, svc.DisplayName));
                    else
                        problems.Add(ActiveProblem.ServiceDown(name, svc.DisplayName, svc.StartupType));
                }
            }
        }

        // Derive problems from Event Log
        if (eventLogs != null && eventLogs.Status >= StateMetricStatus.Warning)
        {
            var isCritical = eventLogs.Status == StateMetricStatus.Critical;
            problems.Add(ActiveProblem.EventLogErrors(string.Empty, eventLogs.EventCount, isCritical));
        }

        // Derive problems from SQL query checks
        if (sqlQueryChecks != null)
        {
            foreach (var (_, check) in sqlQueryChecks)
            {
                if (!string.IsNullOrEmpty(check.ConnectionError))
                {
                    problems.Add(ActiveProblem.SqlQueryConnectionError(check.CheckId, check.CheckName, check.ConnectionError));
                    continue;
                }

                foreach (var queryResult in check.Results)
                {
                    if (!string.IsNullOrEmpty(queryResult.ExecutionError))
                    {
                        problems.Add(ActiveProblem.SqlQueryExecutionError(
                            check.CheckId, check.CheckName, queryResult.QueryName, queryResult.ExecutionError));
                    }

                    if (queryResult.RowCount is { Passed: false } rc)
                    {
                        problems.Add(ActiveProblem.SqlQueryRowCountMismatch(
                            check.CheckId, check.CheckName, queryResult.QueryName,
                            rc.ActualCount, rc.Operator, rc.ExpectedCount));
                    }

                    foreach (var col in queryResult.ColumnResults.Where(c => !c.Passed))
                    {
                        problems.Add(ActiveProblem.SqlQueryColumnMismatch(
                            check.CheckId, check.CheckName, queryResult.QueryName,
                            col.AssertionName, col.ActualValue, col.Operator, col.ExpectedValue, col.FailureReason));
                    }
                }
            }
        }

        // Derive problems from email probes (sender role)
        if (emailProbes != null)
        {
            foreach (var (_, probe) in emailProbes)
            {
                if (probe.Status == StateMetricStatus.Critical && !string.IsNullOrEmpty(probe.ErrorMessage))
                {
                    problems.Add(ActiveProblem.EmailProbeSmtpError(probe.ProbeId, probe.ProbeName, probe.ErrorMessage));
                }
            }
        }

        // Derive problems from certificates
        if (certificates != null)
        {
            foreach (var (_, cert) in certificates)
            {
                if (cert.Status == StateMetricStatus.Critical || cert.Status == StateMetricStatus.Warning)
                {
                    var isCritical = cert.Status == StateMetricStatus.Critical;
                    var thumb = string.IsNullOrEmpty(cert.Thumbprint) ? "unknown" : cert.Thumbprint;
                    problems.Add(ActiveProblem.CertificateExpiring(cert.Subject, thumb, cert.DaysUntilExpiry, isCritical));
                }
            }
        }

        // Derive problems from email delivery checks (checker role)
        if (emailDelivery != null)
        {
            foreach (var (_, check) in emailDelivery)
            {
                if (check.IsConnectionError && !string.IsNullOrEmpty(check.ErrorMessage))
                {
                    problems.Add(ActiveProblem.EmailDeliveryImapError(check.CheckId, check.CheckName, check.ErrorMessage));
                }
                else if (check.Status == StateMetricStatus.Warning)
                {
                    problems.Add(ActiveProblem.EmailDeliveryStale(check.CheckId, check.CheckName, check.AgeMinutes, isCritical: false));
                }
                else if (check.Status == StateMetricStatus.Critical)
                {
                    // IsNotFound means no messages in mailbox — pass null so the message is "not found" not "0 minutes stale"
                    var age = check.IsNotFound ? null : check.AgeMinutes;
                    problems.Add(ActiveProblem.EmailDeliveryStale(check.CheckId, check.CheckName, age, isCritical: true));
                }
            }
        }

        // Derive problems from BizTalk
        if (bizTalk != null)
        {
            if (!bizTalk.ApiReachable)
            {
                problems.Add(ActiveProblem.BizTalkApiError(bizTalk.ApiError ?? "unbekannt"));
            }
            else
            {
                foreach (var artifact in bizTalk.Problems)
                {
                    problems.Add(artifact.ArtifactType switch
                    {
                        "application" => ActiveProblem.BizTalkApplicationStopped(artifact.Name, artifact.Status),
                        "orchestration" => ActiveProblem.BizTalkOrchestrationStopped(artifact.Name, artifact.Status),
                        "sendport" => ActiveProblem.BizTalkSendPortStopped(artifact.Name, artifact.Status),
                        "receivelocation" => ActiveProblem.BizTalkReceiveLocationDisabled(artifact.Name, artifact.IsCritical),
                        _ => ActiveProblem.BizTalkApplicationStopped(artifact.Name, artifact.Status)
                    });
                }

                if (bizTalk.SuspendedStatus is StateMetricStatus.Critical or StateMetricStatus.Warning)
                {
                    problems.Add(ActiveProblem.BizTalkSuspendedInstances(
                        bizTalk.SuspendedTotal, bizTalk.SuspendedStatus == StateMetricStatus.Critical));
                }
            }
        }

        // Derive problems from file-share / log monitoring
        if (fileMonitoring != null)
        {
            if (fileMonitoring.AnyError && !string.IsNullOrEmpty(fileMonitoring.Error))
            {
                problems.Add(ActiveProblem.FileShareUnreachable("Fileshare", fileMonitoring.Error));
            }

            foreach (var stuck in fileMonitoring.StuckFiles)
            {
                problems.Add(ActiveProblem.FileStuck(
                    stuck.Directory, stuck.FilePath, stuck.AgeMinutes, stuck.Reason, stuck.IsCritical,
                    stuck.WikiUrl, stuck.RemediationSteps));
            }

            foreach (var match in fileMonitoring.LogMatches)
            {
                problems.Add(ActiveProblem.LogError(
                    match.LogScan, match.LogPath, match.Pattern, match.Count, match.SampleLine, match.IsCritical,
                    match.WikiUrl, match.RemediationSteps));
            }
        }

        return problems;
    }

    /// <summary>
    /// Derives problems and applies active acknowledges for the matching server (backward-compatible overload).
    /// </summary>
    public static List<ActiveProblem> DeriveProblems(
        List<Metric> metrics,
        Dictionary<string, AppPoolState>? appPools,
        Dictionary<string, ServiceState>? services,
        IEnumerable<Acknowledge>? acknowledges,
        string serverId)
        => DeriveProblems(metrics, appPools, services, null, null, acknowledges, serverId);

    /// <summary>
    /// Derives problems and applies active acknowledges for the matching server.
    /// </summary>
    public static List<ActiveProblem> DeriveProblems(
        List<Metric> metrics,
        Dictionary<string, AppPoolState>? appPools,
        Dictionary<string, ServiceState>? services,
        EventLogState? eventLogs,
        IEnumerable<Acknowledge>? acknowledges,
        string serverId)
        => DeriveProblems(metrics, appPools, services, eventLogs, null, acknowledges, serverId);

    /// <summary>
    /// Derives problems from all checker states and applies active acknowledges.
    /// </summary>
    public static List<ActiveProblem> DeriveProblems(
        List<Metric> metrics,
        Dictionary<string, AppPoolState>? appPools,
        Dictionary<string, ServiceState>? services,
        EventLogState? eventLogs,
        Dictionary<string, SqlQueryCheckState>? sqlQueryChecks,
        IEnumerable<Acknowledge>? acknowledges,
        string serverId,
        Dictionary<string, EmailProbeState>? emailProbes = null,
        Dictionary<string, EmailDeliveryCheckState>? emailDelivery = null,
        Dictionary<string, CertificateState>? certificates = null,
        BizTalkState? bizTalk = null,
        FileMonitoringState? fileMonitoring = null)
    {
        var problems = DeriveProblems(metrics, appPools, services, eventLogs, sqlQueryChecks, emailProbes, emailDelivery, certificates, bizTalk, fileMonitoring);

        if (acknowledges != null)
        {
            ApplyAcknowledges(problems, acknowledges, serverId);
        }

        return problems;
    }

    /// <summary>
    /// Applies acknowledge information to derived problems by matching problem IDs
    /// </summary>
    public static void ApplyAcknowledges(
        List<ActiveProblem> problems,
        IEnumerable<Acknowledge> acknowledges,
        string? serverId = null)
    {
        var ackByProblemId = new Dictionary<string, Acknowledge>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var ack in acknowledges)
        {
            // Expired entries stay "Active" until a worker processes them — they must not suppress anything.
            if (!ack.IsEffective(now) || string.IsNullOrEmpty(ack.ProblemId))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(serverId) &&
                !string.Equals(ack.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ackByProblemId[ack.ProblemId] = ack;
        }

        foreach (var problem in problems)
        {
            if (ackByProblemId.TryGetValue(problem.Id, out var ack))
            {
                problem.Acknowledged = true;
                problem.AcknowledgeId = ack.Id;
                problem.AcknowledgeExpiresAt = ack.ExpiresAt;
                problem.AcknowledgedBy = ack.AcknowledgedBy;
            }
        }
    }
}
