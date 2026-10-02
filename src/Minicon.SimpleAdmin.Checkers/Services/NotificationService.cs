using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Central notifier. Runs on the servers listed in <c>features.notifications.notifierServers</c>
/// (no <c>--notify</c> flag). Aggregates the status of all servers over HTTP, derives unacknowledged
/// problems, and sends one summary e-mail per cycle with acknowledge deep-links. All HTTP URLs
/// (status, peers, acknowledges, deep-link base) are derived from the configured servers' BaseUrls.
/// Two notifier servers coordinate via per-instance notify-state.json files published over HTTP:
/// claim-first (LastAttemptAt written before sending) plus staggered schedules avoid double-sends
/// without a shared lock.
/// </summary>
public interface INotificationService
{
    /// <summary>Runs one notifier cycle. Returns the number of mails sent (problem/all-clear mail plus an optional scheduled status report).</summary>
    Task<int> RunCycleAsync(Config config, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class NotificationService(
    ILogger<NotificationService> logger,
    IHttpStatusReader statusReader,
    NotificationStateStore stateStore,
    ISmtpMailSender mailSender,
    IDateTimeProvider dateTime,
    string statusDirectory,
    string masterKey,
    string instanceId,
    string? wwwrootDirectory = null) : INotificationService
{
    /// <summary>
    /// Saves the canonical state and, when a wwwroot is known, additionally publishes a
    /// copy to {wwwroot}/status/notify-state.json so the peer notifier can read it over
    /// HTTP (claim-first dedup). The canonical location stays unchanged — no migration.
    /// </summary>
    private async Task SaveAndPublishStateAsync(string localPath, NotifyState state, CancellationToken cancellationToken)
    {
        await stateStore.SaveAsync(localPath, state, cancellationToken);

        if (string.IsNullOrWhiteSpace(wwwrootDirectory))
            return;

        try
        {
            var publishPath = Path.Combine(wwwrootDirectory, "status", "notify-state.json");
            if (!string.Equals(Path.GetFullPath(publishPath), Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase))
                await stateStore.SaveAsync(publishPath, state, cancellationToken);
        }
        catch (Exception ex)
        {
            // Publishing is best-effort — peer dedup degrades gracefully without it.
            logger.LogWarning(ex, "Notifier: could not publish notify-state.json to wwwroot");
        }
    }

    public async Task<int> RunCycleAsync(Config config, CancellationToken cancellationToken = default)
    {
        var notif = config.Features?.Notifications;
        if (notif == null || !notif.Enabled)
        {
            logger.LogDebug("Notifications disabled");
            return 0;
        }

        var now = dateTime.UtcNow;
        var minSeverity = ParseSeverity(notif.MinSeverity);

        // 1+2. Aggregate all servers over HTTP and load acknowledges, then derive problems.
        // The published acknowledges.json is only refreshed on WebUI mutations, so filter out
        // entries that have expired since — otherwise a lapsed acknowledge would still suppress mails.
        var acknowledgesUrl = ResolveAcknowledgesUrl(config, notif);
        var acknowledges = (string.IsNullOrWhiteSpace(acknowledgesUrl)
                ? new List<Acknowledge>()
                : await statusReader.FetchActiveAcknowledgesAsync(acknowledgesUrl, cancellationToken))
            .Where(a => !a.ExpiresAt.HasValue || a.ExpiresAt.Value > now)
            .ToList();

        // 4a. Load own + peer notify-state for cross-instance dedup (also decides whether a status report is due).
        var localPath = ResolveLocalStatePath(notif);
        var ownState = await stateStore.LoadAsync(localPath, cancellationToken);
        var peerStates = await LoadPeerStatesAsync(config, notif, cancellationToken);

        var retryAfter = TimeSpan.FromMinutes(Math.Max(1, notif.RetryAfterMinutes));
        var reportDue = IsStatusReportDue(notif.StatusReport, now, retryAfter, ownState, peerStates, TimeZoneInfo.Local);

        // The status report always lists unreachable/stale servers, even when notifyOnStaleServer is off.
        var (problems, polledServers) = await CollectProblemsAsync(
            config, notif, acknowledges, now, includeStale: notif.NotifyOnStaleServer || reportDue, cancellationToken);

        // 3. Scope filter: unacknowledged and at/above the configured minimum severity.
        var candidates = problems
            .Where(p => !p.Problem.Acknowledged && (int)p.Problem.Severity >= (int)minSeverity)
            .Where(p => notif.NotifyOnStaleServer || !IsServerStale(p.Problem))
            .ToList();

        // Set of server-scoped problem IDs that are currently open — drives reminders/pruning.
        var openProblemIds = new HashSet<string>(candidates.Select(p => GetStateKey(p.ServerId, p.Problem.Id)), StringComparer.OrdinalIgnoreCase);

        // 4b. Due decision per problem.
        var reNotify = TimeSpan.FromHours(Math.Max(1, notif.ReNotifyIntervalHours));

        // Decide which candidates are due (new, reminder, or retry after a failed send).
        var due = candidates
            .Where(p => IsDue(p.ServerId, p.Problem.Id, now, reNotify, retryAfter, ownState, peerStates))
            .ToList();

        // 5. Determine all-clear candidates: previously notified, no longer open, not yet cleared by anyone.
        var resolved = notif.SendResolvedNotice
            ? FindResolvedEntries(ownState, peerStates, openProblemIds)
            : new List<(string ServerId, NotifyEntry Entry, string ProblemId)>();

        var mailsSent = 0;
        if (due.Count > 0 || resolved.Count > 0)
        {
            mailsSent = await SendCycleMailAsync(config, notif, due, resolved, ownState, localPath, now, cancellationToken);
        }

        // 5b. Scheduled status report (independent of the problem mail above).
        if (reportDue)
        {
            mailsSent += await SendStatusReportAsync(config, notif, problems, polledServers, ownState, localPath, now, cancellationToken);
        }

        // 6. Prune state entries that are no longer open and have been cleared (or clearing disabled).
        PruneState(ownState, openProblemIds, notif.SendResolvedNotice);
        ownState.GeneratedBy = instanceId;
        ownState.GeneratedAt = now;
        await SaveAndPublishStateAsync(localPath, ownState, cancellationToken);

        return mailsSent;
    }

    // --- problem collection -------------------------------------------------

    private async Task<(List<(string ServerId, ActiveProblem Problem)> Problems, List<string> PolledServers)> CollectProblemsAsync(
        Config config,
        NotificationsFeatureConfig notif,
        List<Acknowledge> acknowledges,
        DateTime now,
        bool includeStale,
        CancellationToken cancellationToken)
    {
        var staleThreshold = TimeSpan.FromMinutes(Math.Max(1, notif.StaleThresholdMinutes));
        var collected = new List<(string ServerId, ActiveProblem Problem)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // serverId|problemId
        var polled = new List<string>();

        foreach (var (serverName, url) in BuildStatusSources(config, notif))
        {
            if (!polled.Contains(serverName, StringComparer.OrdinalIgnoreCase))
                polled.Add(serverName);

            var central = await statusReader.FetchServerStatusAsync(url, cancellationToken);

            if (central == null)
            {
                if (includeStale)
                    AddProblem(collected, seen, serverName, ActiveProblem.ServerStale(serverName, null, unreachable: true));
                continue;
            }

            var age = now - central.GeneratedAt;
            if (age > staleThreshold)
            {
                if (includeStale)
                    AddProblem(collected, seen, serverName, ActiveProblem.ServerStale(serverName, age.TotalMinutes, unreachable: false));
                continue;
            }

            foreach (var status in central.Statuses)
            {
                var serverId = string.IsNullOrEmpty(status.Server) ? serverName : status.Server;
                // Central ignore rules: an event ignored in the central WebUI must not be mailed.
                var eventLogConfig = config.Features?.EventLog;
                EventLogIgnoreMatcher.ApplyCentral(status.EventLogs, serverId, eventLogConfig?.IgnoreRules, eventLogConfig?.Defaults, now);
                var derived = ProblemDerivationService.DeriveProblems(
                    status.Metrics ?? new List<Metric>(),
                    status.AppPools,
                    status.Services,
                    status.EventLogs,
                    status.SqlQueryChecks,
                    status.EmailProbes,
                    status.EmailDelivery,
                    status.Certificates,
                    status.BizTalk,
                    status.FileMonitoring);

                foreach (var problem in derived)
                    AddProblem(collected, seen, serverId, problem);
            }
        }

        // Apply acknowledges per server group (sets Acknowledged + AcknowledgeId).
        foreach (var group in collected.GroupBy(c => c.ServerId, StringComparer.OrdinalIgnoreCase))
        {
            ProblemDerivationService.ApplyAcknowledges(
                group.Select(g => g.Problem).ToList(), acknowledges, group.Key);
        }

        return (collected, polled);
    }

    private static bool IsServerStale(ActiveProblem problem)
        => problem.Type == "server" && problem.Source == "Notifier";

    private static void AddProblem(
        List<(string ServerId, ActiveProblem Problem)> collected,
        HashSet<string> seen,
        string serverId,
        ActiveProblem problem)
    {
        if (seen.Add($"{serverId}|{problem.Id}"))
            collected.Add((serverId, problem));
    }

    /// <summary>
    /// Builds (serverName, statusUrl) pairs. Explicit StatusUrls win (server name taken from the
    /// file name); otherwise URLs are derived from each active server's BaseUrl.
    /// </summary>
    private IEnumerable<(string ServerName, string Url)> BuildStatusSources(Config config, NotificationsFeatureConfig notif)
    {
        if (notif.StatusUrls.Count > 0)
        {
            foreach (var url in notif.StatusUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
                yield return (ExtractServerNameFromUrl(url), url);
            yield break;
        }

        foreach (var server in config.Servers.Where(s => s.Active))
        {
            if (string.IsNullOrWhiteSpace(server.BaseUrl))
            {
                logger.LogWarning("Notifier: server '{Server}' has no baseUrl and is not in statusUrls — skipped", server.Name);
                continue;
            }

            var baseUrl = server.BaseUrl!.TrimEnd('/');
            yield return (server.Name, $"{baseUrl}/status/{server.Name}.status.json");
        }
    }

    private static string ExtractServerNameFromUrl(string url)
    {
        var fileName = url.Split('/', '\\').LastOrDefault() ?? url;
        const string suffix = ".status.json";
        if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return fileName[..^suffix.Length];
        var dot = fileName.IndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    // --- due / dedup decision ----------------------------------------------

    private static bool IsDue(
        string serverId,
        string problemId,
        DateTime now,
        TimeSpan reNotify,
        TimeSpan retryAfter,
        NotifyState ownState,
        IReadOnlyList<NotifyState> peerStates)
    {
        DateTime? lastAttempt = null;
        DateTime? lastNotified = null;

        foreach (var state in Prepend(ownState, peerStates))
        {
            if (!TryGetEntry(state, serverId, problemId, out var entry))
                continue;
            if (entry.LastAttemptAt != default && (lastAttempt == null || entry.LastAttemptAt > lastAttempt))
                lastAttempt = entry.LastAttemptAt;
            if (entry.LastNotifiedAt.HasValue && (lastNotified == null || entry.LastNotifiedAt > lastNotified))
                lastNotified = entry.LastNotifiedAt;
        }

        if (lastAttempt == null)
            return true; // never attempted by anyone → new problem

        if (lastNotified.HasValue)
            return (now - lastNotified.Value) >= reNotify; // reminder

        // Attempted but never succeeded → retry once the retry window has elapsed.
        return (now - lastAttempt.Value) >= retryAfter;
    }

    private List<(string ServerId, NotifyEntry Entry, string ProblemId)> FindResolvedEntries(
        NotifyState ownState,
        IReadOnlyList<NotifyState> peerStates,
        HashSet<string> openProblemIds)
    {
        var result = new List<(string, NotifyEntry, string)>();
        foreach (var (storedKey, entry) in ownState.Entries)
        {
            var problemId = GetProblemId(storedKey, entry);
            if (openProblemIds.Contains(GetStateKey(entry.ServerId, problemId)))
                continue; // still open
            if (!entry.LastNotifiedAt.HasValue)
                continue; // never actually notified — nothing to clear
            if (entry.ResolvedNoticeSent)
                continue;
            if (peerStates.Any(s => TryGetEntry(s, entry.ServerId, problemId, out var pe) && pe.ResolvedNoticeSent))
                continue; // a peer already sent the all-clear
            result.Add((entry.ServerId, entry, problemId));
        }
        return result;
    }

    private static void PruneState(NotifyState ownState, HashSet<string> openProblemIds, bool sendResolvedNotice)
    {
        var toRemove = ownState.Entries
            .Where(kv => !openProblemIds.Contains(GetStateKey(kv.Value.ServerId, GetProblemId(kv.Key, kv.Value)))
                         && (!sendResolvedNotice || kv.Value.ResolvedNoticeSent || !kv.Value.LastNotifiedAt.HasValue))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in toRemove)
            ownState.Entries.Remove(key);
    }

    // --- mail ---------------------------------------------------------------

    private async Task<int> SendCycleMailAsync(
        Config config,
        NotificationsFeatureConfig notif,
        List<(string ServerId, ActiveProblem Problem)> due,
        List<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        NotifyState ownState,
        string localPath,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var smtp = ResolveSmtp(config, notif.SmtpRef);
        if (smtp == null)
        {
            logger.LogWarning("Notifier: {Count} problem(s) due but no SMTP probe resolved (smtpRef='{Ref}')", due.Count, notif.SmtpRef);
            return 0;
        }

        var recipients = ParseRecipients(notif.Recipients);
        if (recipients.Count == 0)
            recipients = ParseRecipients(smtp.To);
        if (recipients.Count == 0)
        {
            logger.LogWarning("Notifier: problems due but no recipients configured");
            return 0;
        }

        // claim-first: record the attempt BEFORE sending so a peer (or a crash mid-send) cannot trigger a duplicate.
        foreach (var (serverId, problem) in due)
        {
            var stateKey = GetStateKey(serverId, problem.Id);
            if (!ownState.Entries.TryGetValue(stateKey, out var entry))
            {
                if (ownState.Entries.TryGetValue(problem.Id, out var legacyEntry)
                    && string.Equals(legacyEntry.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                {
                    ownState.Entries.Remove(problem.Id);
                    entry = legacyEntry;
                }
                else
                {
                    entry = new NotifyEntry { ServerId = serverId, FirstSeenAt = now };
                }
                ownState.Entries[stateKey] = entry;
            }
            entry.ServerId = serverId;
            entry.ProblemId = problem.Id;
            entry.Severity = problem.Severity.ToString();
            entry.Type = problem.Type;
            entry.Message = problem.Message; // remembered so the all-clear can state exactly what was cleared
            entry.LastAttemptAt = now;
        }
        ownState.GeneratedBy = instanceId;
        ownState.GeneratedAt = now;
        await SaveAndPublishStateAsync(localPath, ownState, cancellationToken);

        var subject = BuildSubject(notif.SubjectPrefix, due, resolved);
        var webUiBaseUrl = ResolveWebUiBaseUrl(config, notif);

        try
        {
            // Render inside the try: a renderer error must count as a failed attempt
            // (retry via RetryAfterMinutes) instead of propagating out of the cycle.
            var mail = NotificationMailRenderer.Render(notif, due, resolved, now, webUiBaseUrl);

            await mailSender.SendAsync(new SmtpMailRequest
            {
                Smtp = smtp,
                Recipients = recipients,
                Subject = subject,
                Body = mail.PlainText,
                // plainTextOnly: escape hatch for content filters that swallow multipart/HTML
                HtmlBody = notif.PlainTextOnly ? null : mail.Html,
                MasterKey = masterKey,
                ContextLabel = "Notifier"
            }, cancellationToken);

            foreach (var (serverId, problem) in due)
            {
                if (ownState.Entries.TryGetValue(GetStateKey(serverId, problem.Id), out var entry))
                {
                    entry.LastNotifiedAt = now;
                    entry.NotifyCount++;
                }
            }
            foreach (var (serverId, _, problemId) in resolved)
            {
                if (ownState.Entries.TryGetValue(GetStateKey(serverId, problemId), out var entry))
                    entry.ResolvedNoticeSent = true;
            }

            logger.LogInformation("Notifier: sent summary mail — {Due} problem(s), {Resolved} cleared, to {Recipients} recipient(s)",
                due.Count, resolved.Count, recipients.Count);
            return 1;
        }
        catch (Exception ex)
        {
            // LastAttemptAt stays set (claim) but LastNotifiedAt does not → retried after RetryAfterMinutes.
            logger.LogError(ex, "Notifier: failed to send summary mail");
            return 0;
        }
    }

    private string BuildSubject(
        string prefix,
        List<(string ServerId, ActiveProblem Problem)> due,
        List<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved)
    {
        prefix = string.IsNullOrWhiteSpace(prefix) ? "[SimpleAdmin]" : prefix.Trim();

        if (due.Count == 0)
            return $"{prefix} Entwarnung — {resolved.Count} Problem(e) behoben";

        var servers = due.Select(d => d.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var critical = due.Count(d => d.Problem.Severity == ProblemSeverity.Critical);
        var warning = due.Count(d => d.Problem.Severity == ProblemSeverity.Warning);
        return $"{prefix} {servers} Server betroffen — {critical} kritisch, {warning} Warnung";
    }

    // --- scheduled status report -------------------------------------------

    /// <summary>
    /// A report is due when the latest scheduled slot (local time) has passed, lies within the
    /// catch-up window and no instance (own or peer) has sent a report since. An attempt after the
    /// slot without success is retried after <paramref name="retryAfter"/> (claim-first, like problems).
    /// </summary>
    internal static bool IsStatusReportDue(
        StatusReportConfig? report,
        DateTime nowUtc,
        TimeSpan retryAfter,
        NotifyState ownState,
        IReadOnlyList<NotifyState> peerStates,
        TimeZoneInfo timeZone)
    {
        if (report == null || !report.Enabled)
            return false;

        var slot = GetLatestReportSlotUtc(ParseReportTimes(report.Times), nowUtc, timeZone);
        if (slot == null)
            return false;
        if (nowUtc - slot.Value > TimeSpan.FromMinutes(Math.Max(1, report.CatchUpMinutes)))
            return false; // missed slot — don't send an outdated report

        var states = Prepend(ownState, peerStates).ToList();
        var lastSent = states.Max(s => s.LastReportSentAt);
        if (lastSent >= slot)
            return false;

        var lastAttempt = states.Max(s => s.LastReportAttemptAt);
        if (lastAttempt == null || lastAttempt < slot)
            return true;

        return nowUtc - lastAttempt.Value >= retryAfter;
    }

    internal static List<TimeOnly> ParseReportTimes(IEnumerable<string>? raw)
    {
        var result = new List<TimeOnly>();
        foreach (var part in (raw ?? Enumerable.Empty<string>())
                     .SelectMany(r => (r ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (TimeOnly.TryParseExact(part, new[] { "H:mm", "HH:mm" }, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t) && !result.Contains(t))
                result.Add(t);
        }
        return result;
    }

    /// <summary>Latest configured slot at or before now (today or yesterday), as UTC. Null when no times are configured.</summary>
    internal static DateTime? GetLatestReportSlotUtc(IReadOnlyList<TimeOnly> times, DateTime nowUtc, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), timeZone);
        DateTime? best = null;
        foreach (var day in new[] { localNow.Date, localNow.Date.AddDays(-1) })
        {
            foreach (var t in times)
            {
                var local = DateTime.SpecifyKind(day + t.ToTimeSpan(), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(local))
                    continue; // skipped by a DST jump
                if (local <= localNow && (best == null || local > best))
                    best = local;
            }
        }
        return best == null ? null : TimeZoneInfo.ConvertTimeToUtc(best.Value, timeZone);
    }

    private async Task<int> SendStatusReportAsync(
        Config config,
        NotificationsFeatureConfig notif,
        List<(string ServerId, ActiveProblem Problem)> problems,
        List<string> polledServers,
        NotifyState ownState,
        string localPath,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var smtp = ResolveSmtp(config, notif.SmtpRef);
        if (smtp == null)
        {
            logger.LogWarning("Notifier: status report due but no SMTP probe resolved (smtpRef='{Ref}')", notif.SmtpRef);
            return 0;
        }

        var recipients = ParseRecipients(notif.Recipients);
        if (recipients.Count == 0)
            recipients = ParseRecipients(smtp.To);
        if (recipients.Count == 0)
        {
            logger.LogWarning("Notifier: status report due but no recipients configured");
            return 0;
        }

        // The report covers warnings and errors, independent of minSeverity.
        var relevant = problems.Where(p => p.Problem.Severity >= ProblemSeverity.Warning).ToList();
        var open = relevant.Where(p => !p.Problem.Acknowledged).ToList();
        var acknowledged = relevant.Where(p => p.Problem.Acknowledged).ToList();
        var serverNames = polledServers
            .Concat(relevant.Select(p => p.ServerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // claim-first
        ownState.LastReportAttemptAt = now;
        ownState.GeneratedBy = instanceId;
        ownState.GeneratedAt = now;
        await SaveAndPublishStateAsync(localPath, ownState, cancellationToken);

        var subject = BuildStatusReportSubject(notif.SubjectPrefix, serverNames.Count, open);

        try
        {
            var mail = NotificationMailRenderer.RenderStatusReport(open, acknowledged, serverNames, now, ResolveWebUiBaseUrl(config, notif));
            var plainTextOnly = notif.StatusReport.PlainTextOnly ?? notif.PlainTextOnly;

            await mailSender.SendAsync(new SmtpMailRequest
            {
                Smtp = smtp,
                Recipients = recipients,
                Subject = subject,
                Body = mail.PlainText,
                HtmlBody = plainTextOnly ? null : mail.Html,
                MasterKey = masterKey,
                ContextLabel = "Notifier-Statusbericht"
            }, cancellationToken);

            ownState.LastReportSentAt = now;
            logger.LogInformation("Notifier: sent status report — {Servers} server(s), {Open} open problem(s), to {Recipients} recipient(s)",
                serverNames.Count, open.Count, recipients.Count);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notifier: failed to send status report");
            return 0;
        }
    }

    private static string BuildStatusReportSubject(string prefix, int serverCount, List<(string ServerId, ActiveProblem Problem)> open)
    {
        prefix = string.IsNullOrWhiteSpace(prefix) ? "[SimpleAdmin]" : prefix.Trim();
        if (open.Count == 0)
            return $"{prefix} Statusbericht — alles OK ({serverCount} Server)";

        var affected = open.Select(o => o.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var critical = open.Count(o => o.Problem.Severity == ProblemSeverity.Critical);
        var warning = open.Count(o => o.Problem.Severity == ProblemSeverity.Warning);
        return $"{prefix} Statusbericht — {affected} von {serverCount} Servern mit Problemen ({critical} kritisch, {warning} Warnung)";
    }

    // --- helpers ------------------------------------------------------------

    private async Task<IReadOnlyList<NotifyState>> LoadPeerStatesAsync(Config config, NotificationsFeatureConfig notif, CancellationToken cancellationToken)
    {
        var result = new List<NotifyState>();
        foreach (var url in ResolvePeerUrls(config, notif))
        {
            var state = await statusReader.FetchNotifyStateAsync(url, cancellationToken);
            if (state != null)
                result.Add(state);
        }
        return result;
    }

    private string ResolveLocalStatePath(NotificationsFeatureConfig notif)
        => string.IsNullOrWhiteSpace(notif.LocalNotifyStatePath)
            ? Path.Combine(statusDirectory, "notify-state.json")
            : notif.LocalNotifyStatePath;

    // --- URL derivation (explicit override wins, otherwise derived from the configured servers) ----

    /// <summary>Resolves the acknowledges.json URL: explicit override, else {webUiBaseUrl}/status/acknowledges.json.</summary>
    private string ResolveAcknowledgesUrl(Config config, NotificationsFeatureConfig notif)
    {
        if (!string.IsNullOrWhiteSpace(notif.AcknowledgesUrl))
            return notif.AcknowledgesUrl;
        var baseUrl = ResolveWebUiBaseUrl(config, notif);
        return string.IsNullOrEmpty(baseUrl) ? string.Empty : $"{baseUrl}/status/acknowledges.json";
    }

    /// <summary>Resolves the WebUI base URL for deep-links: explicit override, else WebUiServer's BaseUrl
    /// (defaulting to the first NotifierServer).</summary>
    private string ResolveWebUiBaseUrl(Config config, NotificationsFeatureConfig notif)
    {
        if (!string.IsNullOrWhiteSpace(notif.WebUiBaseUrl))
            return notif.WebUiBaseUrl.TrimEnd('/');
        var serverName = !string.IsNullOrWhiteSpace(notif.WebUiServer)
            ? notif.WebUiServer
            : notif.NotifierServers.FirstOrDefault();
        return GetServerBaseUrl(config, serverName) ?? string.Empty;
    }

    /// <summary>Resolves the peer notify-state URLs: explicit override, else the OTHER notifier servers'
    /// BaseUrls (this host excluded).</summary>
    private IEnumerable<string> ResolvePeerUrls(Config config, NotificationsFeatureConfig notif)
    {
        if (notif.PeerNotifyStateUrls.Count > 0)
            return notif.PeerNotifyStateUrls.Where(u => !string.IsNullOrWhiteSpace(u));

        return notif.NotifierServers
            .Where(name => !string.Equals(name, instanceId, StringComparison.OrdinalIgnoreCase))
            .Select(name => GetServerBaseUrl(config, name))
            .Where(b => !string.IsNullOrEmpty(b))
            .Select(b => $"{b}/status/notify-state.json");
    }

    private static string? GetServerBaseUrl(Config config, string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return null;
        var server = config.Servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(server?.BaseUrl) ? null : server!.BaseUrl!.TrimEnd('/');
    }

    private static ProblemSeverity ParseSeverity(string? value)
        => Enum.TryParse<ProblemSeverity>(value, ignoreCase: true, out var s) ? s : ProblemSeverity.Warning;

    private static IEnumerable<NotifyState> Prepend(NotifyState own, IReadOnlyList<NotifyState> peers)
    {
        yield return own;
        foreach (var p in peers)
            yield return p;
    }

    private static string GetStateKey(string serverId, string problemId) => $"{serverId}|{problemId}";

    private static string GetProblemId(string stateKey, NotifyEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ProblemId))
            return entry.ProblemId;

        var separator = stateKey.IndexOf('|');
        return separator >= 0 ? stateKey[(separator + 1)..] : stateKey;
    }

    private static bool TryGetEntry(NotifyState state, string serverId, string problemId, out NotifyEntry entry)
    {
        if (state.Entries.TryGetValue(GetStateKey(serverId, problemId), out entry!))
            return true;

        // States from before server-scoped keys used the raw problem ID. They remain valid only
        // for the server recorded in the entry; never let one server suppress another one.
        return state.Entries.TryGetValue(problemId, out entry!)
            && string.Equals(entry.ServerId, serverId, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseRecipients(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('@'))
            .ToList();
    }

    private SmtpConfig? ResolveSmtp(Config config, string smtpRef)
    {
        // Format "ServerId:ProbeName" — falls back to first available probe across all servers.
        string? wantServer = null, wantProbe = null;
        if (!string.IsNullOrWhiteSpace(smtpRef))
        {
            var parts = smtpRef.Split(':', 2);
            wantServer = parts[0];
            if (parts.Length == 2) wantProbe = parts[1];
        }

        foreach (var server in config.Servers)
        {
            if (wantServer != null && !string.Equals(server.Name, wantServer, StringComparison.OrdinalIgnoreCase))
                continue;

            var probes = server.Checks?.EmailProbes?.Probes;
            if (probes == null || probes.Count == 0) continue;

            var probe = wantProbe != null
                ? probes.FirstOrDefault(p => string.Equals(p.Name, wantProbe, StringComparison.OrdinalIgnoreCase))
                : probes[0];

            if (probe != null)
                return probe.Smtp;
        }

        return null;
    }
}
