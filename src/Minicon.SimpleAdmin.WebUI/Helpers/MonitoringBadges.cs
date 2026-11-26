using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using MetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;
using RuntimeStatus = Minicon.SimpleAdmin.Models.Status.RuntimeStatus;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Helpers;

internal static class MonitoringBadges
{
    public static IReadOnlyList<MonitoringBadge> Create(Config config, ServerStatusView status)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(status);

        var server = config.Servers.FirstOrDefault(s =>
                         string.Equals(s.GetEffectiveId(), status.ServerId, StringComparison.OrdinalIgnoreCase))
                     ?? config.Servers.FirstOrDefault(s =>
                         string.Equals(s.Name, status.ServerId, StringComparison.OrdinalIgnoreCase));
        if (server is not { Active: true })
        {
            return [];
        }

        var features = config.GetEffectiveFeatures();
        var checks = server.Checks;
        var badges = new List<MonitoringBadge>();

        if (features.AppPools?.Enabled == true && checks?.AppPools?.Enabled == true)
        {
            badges.Add(AppPools(status));
        }
        if (features.Services?.Enabled == true && checks?.Services is { Enabled: true } services
            && services.Checks?.Count > 0)
        {
            badges.Add(WindowsServices(status));
        }
        if (features.EventLog?.Enabled == true && checks?.EventLog?.Enabled == true)
        {
            badges.Add(EventLogs(status));
        }
        if (features.SqlQueries?.Enabled == true && checks?.SqlQueries is { Enabled: true } sql
            && sql.Checks?.Count > 0)
        {
            badges.Add(SqlQueries(status));
        }
        if (checks?.EmailProbes is { Enabled: true } probes && probes.Probes?.Count > 0)
        {
            badges.Add(EmailProbes(status));
        }
        if (checks?.EmailDelivery is { Enabled: true } delivery && delivery.Checks?.Count > 0)
        {
            badges.Add(EmailDelivery(status));
        }
        if (features.Certificates?.Enabled == true && checks?.Certificates is { Enabled: true } certificates
            && certificates.Checks?.Count > 0)
        {
            badges.Add(Certificates(status));
        }
        if (features.BizTalk?.Enabled == true && checks?.BizTalk?.Enabled == true)
        {
            badges.Add(BizTalk(status));
        }
        if (features.FileMonitoring?.Enabled == true && checks?.FileMonitoring is { Enabled: true } files
            && (files.Directories?.Count > 0 || files.LogScans?.Count > 0))
        {
            badges.Add(FileMonitoring(status));
        }
        if (ScheduledTaskGroupEvaluator.IsParticipant(features, server))
        {
            badges.Add(ScheduledTasks(status));
        }

        return badges.Select(badge => badge with { Fragment = "#" + DetailsSectionId(badge.Key) }).ToList();
    }

    internal static string DetailsSectionId(string badgeKey) => badgeKey switch
    {
        "apppools" => "app-pools",
        "services" => "windows-services",
        "eventlog" => "event-log",
        "sql" => "sql-queries",
        "emailprobes" => "email-probes",
        "emaildelivery" => "email-delivery",
        "certificates" => "certificates",
        "biztalk" => "biztalk",
        "files" => "file-monitoring",
        "scheduledtasks" => "scheduled-tasks",
        _ => throw new ArgumentOutOfRangeException(nameof(badgeKey), badgeKey, null)
    };

    private static MonitoringBadge AppPools(ServerStatusView status)
    {
        var pools = States(status, s => s.AppPools, s => s.AppPools);
        if (!HasData(status, s => s.AppPools, s => s.AppPools))
        {
            return NoData("apppools", "Pools", "bi-layers", "IIS AppPools");
        }

        var total = pools.Count;
        var healthy = pools.Values.Count(p => p.IsHealthy);
        var running = pools.Values.Count(p => p.Status == AppPoolStatus.Running);
        var critical = pools.Values.Count(p => p.GetOverallStatus() == OverallStatus.Critical);
        var tone = critical > 0 ? "crit" : healthy < total ? "warn" : "ok";
        return new("apppools", $"{healthy}/{total} Pools", tone, "bi-layers",
            $"IIS AppPools: {running} von {total} laufen, {healthy} von {total} gesund");
    }

    private static MonitoringBadge WindowsServices(ServerStatusView status)
    {
        var services = States(status, s => s.WindowsServices, s => s.Services);
        if (!HasData(status, s => s.WindowsServices, s => s.Services))
        {
            return NoData("services", "Dienste", "bi-window", "Windows-Dienste");
        }

        var total = services.Count;
        var running = services.Values.Count(s => string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase));
        return new("services", $"{running}/{total} Dienste", running < total ? "crit" : "ok", "bi-window",
            $"Windows-Dienste: {running} von {total} laufen");
    }

    private static MonitoringBadge EventLogs(ServerStatusView status)
    {
        var eventLogs = status.EventLogs ?? status.Services.Select(s => s.EventLogs).FirstOrDefault(s => s != null);
        if (eventLogs == null)
        {
            return NoData("eventlog", "Events", "bi-journal-text", "Event-Log");
        }

        var ignored = eventLogs.IgnoredCount > 0 ? $", {eventLogs.IgnoredCount} ignoriert" : string.Empty;
        return new("eventlog", $"{eventLogs.EventCount} Events", Tone(eventLogs.Status), "bi-journal-text",
            $"Event-Log: {eventLogs.EventCount} Ereignis(se){ignored}");
    }

    private static MonitoringBadge SqlQueries(ServerStatusView status)
    {
        var checks = status.SqlQueryChecks.Count > 0
            ? status.SqlQueryChecks
            : States(status, _ => null, s => s.SqlQueryChecks);
        if (checks.Count == 0 && !status.Services.Any(s => s.SqlQueryChecks != null))
        {
            return NoData("sql", "SQL", "bi-database", "SQL-Prüfungen");
        }

        return MetricDictionaryBadge("sql", "SQL", "bi-database", "SQL-Prüfungen",
            checks.Values.Select(c => c.Status));
    }

    private static MonitoringBadge EmailProbes(ServerStatusView status)
    {
        var probes = States(status, s => s.EmailProbes, s => s.EmailProbes);
        if (!HasData(status, s => s.EmailProbes, s => s.EmailProbes))
        {
            return NoData("emailprobes", "Probes", "bi-envelope", "E-Mail-Probes");
        }

        return MetricDictionaryBadge("emailprobes", "Probes", "bi-envelope", "E-Mail-Probes",
            probes.Values.Select(p => p.Status));
    }

    private static MonitoringBadge EmailDelivery(ServerStatusView status)
    {
        var delivery = States(status, s => s.EmailDelivery, s => s.EmailDelivery);
        if (!HasData(status, s => s.EmailDelivery, s => s.EmailDelivery))
        {
            return NoData("emaildelivery", "Zustellung", "bi-envelope-check", "E-Mail-Zustellung");
        }

        return MetricDictionaryBadge("emaildelivery", "Zustellung", "bi-envelope-check", "E-Mail-Zustellung",
            delivery.Values.Select(c => c.Status));
    }

    private static MonitoringBadge Certificates(ServerStatusView status)
    {
        var certificates = States(status, s => s.Certificates, s => s.Certificates);
        if (!HasData(status, s => s.Certificates, s => s.Certificates))
        {
            return NoData("certificates", "Zertifikate", "bi-shield-lock", "Zertifikate");
        }

        return MetricDictionaryBadge("certificates", "Zertifikate", "bi-shield-lock", "Zertifikate",
            certificates.Values.Select(c => c.Status));
    }

    private static MonitoringBadge MetricDictionaryBadge(string key, string label, string icon, string description,
        IEnumerable<MetricStatus> sourceStates)
    {
        var states = sourceStates.ToList();
        var critical = states.Count(s => s == MetricStatus.Critical);
        var warning = states.Count(s => s == MetricStatus.Warning);
        var unknown = states.Count(s => s == MetricStatus.Unknown);
        var healthy = states.Count(s => s == MetricStatus.Healthy);
        var tone = critical > 0 ? "crit" : warning > 0 ? "warn" : unknown > 0 ? "muted" : "ok";
        return new(key, $"{healthy}/{states.Count} {label}", tone, icon,
            $"{description}: {healthy} von {states.Count} OK");
    }

    private static MonitoringBadge BizTalk(ServerStatusView status)
    {
        var bizTalk = status.BizTalk ?? status.Services.Select(s => s.BizTalk).FirstOrDefault(s => s != null);
        if (bizTalk == null)
        {
            return NoData("biztalk", "BizTalk", "bi-gear-wide-connected", "BizTalk");
        }
        if (!bizTalk.ApiReachable)
        {
            return new("biztalk", "BizTalk API", "crit", "bi-gear-wide-connected",
                "BizTalk-Management-API nicht erreichbar");
        }

        var total = bizTalk.ApplicationsTotal + bizTalk.OrchestrationsTotal + bizTalk.SendPortsTotal
                    + bizTalk.ReceiveLocationsTotal;
        var critical = bizTalk.Problems.Count(p => p.IsCritical)
                       + (bizTalk.SuspendedStatus == MetricStatus.Critical ? 1 : 0);
        var warning = bizTalk.Problems.Count(p => !p.IsCritical)
                      + (bizTalk.SuspendedStatus == MetricStatus.Warning ? 1 : 0);
        var healthy = Math.Max(0, total - bizTalk.Problems.Count);
        var tone = critical > 0 ? "crit" : warning > 0 ? "warn" : "ok";
        return new("biztalk", $"{healthy}/{total} BizTalk", tone, "bi-gear-wide-connected",
            $"BizTalk: {healthy} von {total} Artefakten OK, {bizTalk.SuspendedTotal} suspendiert");
    }

    private static MonitoringBadge FileMonitoring(ServerStatusView status)
    {
        var files = status.FileMonitoring ?? status.Services.Select(s => s.FileMonitoring).FirstOrDefault(s => s != null);
        if (files == null)
        {
            return NoData("files", "Dateien", "bi-folder-check", "Datei-/Protokollüberwachung");
        }
        if (files.OutsideBusinessHours)
        {
            return new("files", "Dateien pausiert", "muted", "bi-folder-check",
                "Datei-/Protokollüberwachung: außerhalb der Betriebszeiten");
        }

        // The same counter as the static HTML page uses, so that a finding that only exists in one
        // of the two representations (e.g. a missing expected run) can never be shown as "OK".
        var findings = Minicon.SimpleAdmin.Services.FileMonitoringFindingCounter.CountFindings(files);
        var critical = Minicon.SimpleAdmin.Services.FileMonitoringFindingCounter.CountCritical(files);
        var errors = Minicon.SimpleAdmin.Services.FileMonitoringFindingCounter.CountErrors(files);
        var tone = critical > 0 ? "crit" : findings > 0 ? "warn" : "ok";
        var label = errors > 0
            ? $"{findings} Funde, {errors} Fehler"
            : $"{findings} Funde";
        return new("files", label, tone, "bi-folder-check",
            "Datei-/Protokollüberwachung: "
            + Minicon.SimpleAdmin.Services.FileMonitoringFindingCounter.Describe(files));
    }

    private static MonitoringBadge ScheduledTasks(ServerStatusView status)
    {
        var tasks = status.ScheduledTasks ?? status.Services.Select(s => s.ScheduledTasks).FirstOrDefault(s => s != null);
        if (tasks == null)
        {
            return NoData("scheduledtasks", "Tasks", "bi-calendar2-check", "Geplante Tasks");
        }

        if (tasks.QueryFailed)
        {
            return new("scheduledtasks", "Tasks – Abfragefehler", "warn", "bi-calendar2-check",
                "Geplante Tasks konnten nicht abgefragt werden");
        }

        var total = tasks.Tasks.Count;
        var faulty = tasks.Tasks.Count(ScheduledTaskDisplay.HasFinding);
        var healthy = total - faulty;
        var critical = tasks.Tasks.Count(t => t.LastRunFailed && t.FailureIsCritical)
                       + (tasks.EventStatus == MetricStatus.Critical ? 1 : 0);
        var tone = critical > 0 ? "crit"
            : faulty > 0 || tasks.EventStatus == MetricStatus.Warning ? "warn" : "ok";
        return new("scheduledtasks", $"{healthy}/{total} Tasks", tone, "bi-calendar2-check",
            $"Geplante Tasks: {healthy} von {total} OK, {tasks.EventCount} Fehler-Ereignis(se)");
    }

    private static MonitoringBadge NoData(string key, string text, string icon, string description) =>
        new(key, text, "muted", icon, $"{description}: noch keine Daten", HasData: false);

    private static bool HasData<T>(ServerStatusView status, Func<ServerStatusView, T?> aggregated,
        Func<RuntimeStatus, T?> raw) where T : class =>
        aggregated(status) != null || status.Services.Any(s => raw(s) != null);

    private static Dictionary<string, T> States<T>(ServerStatusView status,
        Func<ServerStatusView, Dictionary<string, T>?> aggregated, Func<RuntimeStatus, Dictionary<string, T>?> raw)
    {
        var current = aggregated(status);
        if (current != null)
        {
            return current;
        }

        return status.Services
            .Select(raw)
            .Where(states => states != null)
            .SelectMany(states => states!)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string Tone(MetricStatus status) => status switch
    {
        MetricStatus.Healthy => "ok",
        MetricStatus.Critical => "crit",
        MetricStatus.Warning => "warn",
        _ => "muted"
    };
}

internal sealed record MonitoringBadge(string Key, string Text, string Tone, string Icon, string Title,
    string Fragment = "", bool HasData = true);
