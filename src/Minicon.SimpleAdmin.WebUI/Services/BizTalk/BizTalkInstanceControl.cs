using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services.Control;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

public interface IBizTalkInstanceControl
{
    Task<BizTalkActionResult> ExecuteAsync(string server, Guid id, BizTalkInstanceAction action, string user, CancellationToken ct);
    IReadOnlyList<BizTalkAuditEntry> GetAudit(int max = 1000, string? server = null, Guid? instanceId = null);
}

internal sealed class BizTalkInstanceControl(ConfigurationService config, BizTalkOptions options,
    IBizTalkInstanceProvider provider, IBizTalkAuditLog audit, IActionEventAudit events, TimeProvider clock) : IBizTalkInstanceControl
{
    private readonly KeyedAsyncLock locks = new();
    private static string Account => $"{Environment.UserDomainName}\\{Environment.UserName}";
    private static string Limit(string? value, int max = 1024) => value == null ? "" : value[..Math.Min(value.Length, max)];
    public IReadOnlyList<BizTalkAuditEntry> GetAudit(int max = 1000, string? server = null, Guid? instanceId = null) => audit.Read(max, server, instanceId);

    public async Task<BizTalkActionResult> ExecuteAsync(string server, Guid id, BizTalkInstanceAction action, string user, CancellationToken ct)
    {
        var operation = Guid.NewGuid();
        var target = config.IsLoaded ? BizTalkTargets.Resolve(config.Config, server, options) : null;
        var knownServer = config.IsLoaded ? config.Config.Servers.FirstOrDefault(s =>
            string.Equals(s.GetEffectiveId(), server.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, server.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
        var serverId = knownServer?.GetEffectiveId() ?? "unbekannt";
        BizTalkInstance? instance = null;
        BizTalkAuditEntry Entry(string phase, BizTalkActionOutcome outcome, string message) => new(operation, phase,
            clock.GetUtcNow(), Limit(user, 256), Account, serverId, id, instance?.Application == null ? null : Limit(instance.Application),
            instance?.ApplicationMapping ?? BizTalkApplicationMapping.Unknown, "REST", action, outcome, Limit(message, 2048), Limit(server, 200), knownServer != null);
        async Task<BizTalkActionResult> Finish(BizTalkActionOutcome outcome, string message, int status)
        {
            message = $"{message} Zielserver: {serverId}. Ausführungskonto: {Account}.";
            var entry = Entry("Completed", outcome, message);
            var persisted = await audit.AppendAsync(entry);
            try { events.TryWrite(entry); } catch { /* Event channel must never affect the action. */ }
            if (!persisted) message += " Warnung: Abschluss nicht dauerhaft protokolliert; ein vorhandenes Requested bleibt ungeklärt. Datei-Audit prüfen.";
            return new(operation, serverId, id, action, outcome, message, Account, persisted, null, status);
        }
        if (!options.ControlEnabled) return await Finish(BizTalkActionOutcome.Rejected, options.DisabledReason ?? "Steuerung abgeschaltet.", 403);
        if (id == Guid.Empty || !Enum.IsDefined(action)) return await Finish(BizTalkActionOutcome.Rejected, "Ungültige Instanz/Aktion.", 400);
        if (target == null) return await Finish(BizTalkActionOutcome.Rejected, "Kein aktives, freigegebenes Ziel.", 404);
        using var held = await locks.TryAcquireAsync(serverId + ":" + id.ToString("D"), TimeSpan.FromSeconds(2), ct);
        if (held == null) return await Finish(BizTalkActionOutcome.Rejected, "Aktion gesperrt oder abgebrochen. Frisch prüfen.", 409);
        // Resolve again under the shared Resume/Terminate lock; never retain a browser/status target.
        target = config.IsLoaded ? BizTalkTargets.Resolve(config.Config, serverId, options) : null;
        if (target == null) return await Finish(BizTalkActionOutcome.Rejected, "Ziel wurde deaktiviert oder entfernt.", 404);
        try
        {
            // Query every page in the bounded scan in a single provider call; no client-side application claim.
            var page = await provider.QuerySuspendedAsync(target, new BizTalkInstanceQuery(PageSize: options.EffectiveMaxInstances), ct);
            instance = page.Items.SingleOrDefault(i => i.InstanceId == id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return await Finish(BizTalkActionOutcome.Rejected, "Vor Start abgebrochen.", 409); }
        catch (TimeoutException) { return await Finish(BizTalkActionOutcome.Rejected, "Zeitgrenze der Vorprüfung überschritten.", 504); }
        catch (Exception ex) when (ex is BizTalkProviderException or InvalidOperationException)
        { return await Finish(BizTalkActionOutcome.Failed, "Vorprüfung fehlgeschlagen. Management-API/Rechte prüfen.", 502); }
        if (instance == null) return await Finish(BizTalkActionOutcome.Rejected, "Instanz nicht im sicher gelesenen suspendierten Bereich gefunden.", 404);
        var pending = instance.PendingOperation;
        if ((!string.IsNullOrWhiteSpace(pending) && !pending.Equals("None", StringComparison.OrdinalIgnoreCase) && pending != "0") ||
            (action == BizTalkInstanceAction.Resume && instance.Status != BizTalkInstanceStatus.SuspendedResumable))
            return await Finish(BizTalkActionOutcome.Rejected, "Instanz nicht fortsetzbar oder bereits mit laufender Operation.", 409);
        // Recheck config after IO too; a removed/changed target must not be mutated.
        if (!options.ControlEnabled || BizTalkTargets.Resolve(config.Config, serverId, options) != target)
            return await Finish(BizTalkActionOutcome.Rejected, "Ziel oder Hostfreigabe hat sich geändert.", 409);
        if (ct.IsCancellationRequested) return await Finish(BizTalkActionOutcome.Rejected, "Vor Start abgebrochen.", 409);
        if (!await audit.AppendAsync(Entry("Requested", BizTalkActionOutcome.Unknown, "Angefordert; Ausgang noch nicht abschließend protokolliert.")))
            return await Finish(BizTalkActionOutcome.Rejected, "Pflicht-Datei-Audit nicht schreibbar oder Kapazität offener Aktionen erschöpft. Keine Mutation gestartet.", 503);
        // A file lock/write may have waited while an administrator changed config. Check the destination once more.
        if (!options.ControlEnabled || BizTalkTargets.Resolve(config.Config, serverId, options) != target)
            return await Finish(BizTalkActionOutcome.Rejected, "Ziel oder Hostfreigabe wurde während der Audit-Vorbereitung geändert. Keine Mutation gestartet.", 409);
        // From this point the request's cancellation cannot abandon completion/audit. No retries or fallback.
        using var deadline = new CancellationTokenSource(options.ActionTimeout);
        try
        {
            await provider.ExecuteAsync(target, id, action, deadline.Token);
            return await Finish(BizTalkActionOutcome.Accepted, "Auftrag angenommen; fachliche Verarbeitung nicht bestätigt. Status erneut live prüfen.", 200);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or BizTalkMutationUnknownException)
        { return await Finish(BizTalkActionOutcome.Unknown, "Ausgang unbekannt nach möglicher Übergabe. Nicht automatisch wiederholen; Status frisch prüfen.", 504); }
        catch (BizTalkActionHttpException ex)
        {
            var (outcome, status) = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => (BizTalkActionOutcome.Rejected, 404),
                System.Net.HttpStatusCode.Conflict => (BizTalkActionOutcome.Rejected, 409),
                System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.GatewayTimeout => (BizTalkActionOutcome.Unknown, 504),
                _ => (BizTalkActionOutcome.Failed, 502)
            };
            return await Finish(outcome, ex.Message + (outcome == BizTalkActionOutcome.Unknown ? " Ausgang unbekannt; nicht automatisch wiederholen." : ""), status);
        }
        catch (BizTalkProviderException ex)
        { return await Finish(BizTalkActionOutcome.Failed, ex.Message, 502); }
        catch (Exception)
        { return await Finish(BizTalkActionOutcome.Failed, "Management-API hat die Aktion nicht bestätigt. Rechte/Verbindung prüfen.", 502); }
    }
}
