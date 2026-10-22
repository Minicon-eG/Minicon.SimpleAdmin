using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Executes the BizTalk deactivation requests that the central WebUI has written for one server
/// and reports the outcome back. Runs on the BizTalk server itself, so the Management API is
/// reached with the worker's own (integrated Windows) credentials — the WebUI never needs them.
/// </summary>
public interface IBizTalkActionExecutor
{
    /// <summary>
    /// Executes all pending requests addressed to <paramref name="serverId"/> against
    /// <paramref name="baseUrl"/> (the Management API of this server) and returns the results.
    /// A failing action never aborts the remaining ones.
    /// </summary>
    Task<List<BizTalkActionResult>> ExecutePendingAsync(
        string serverId,
        string baseUrl,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BizTalkActionExecutor(
    IBizTalkApiClient apiClient,
    IBizTalkActionStore store,
    ILogger<BizTalkActionExecutor> logger) : IBizTalkActionExecutor
{
    /// <summary>Identifies this worker process in the claim files.</summary>
    private static readonly string WorkerId =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <inheritdoc />
    public async Task<List<BizTalkActionResult>> ExecutePendingAsync(
        string serverId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BizTalkActionResult>();

        var pending = await store.GetPendingAsync(serverId, cancellationToken);
        if (pending.Count == 0)
            return results;

        logger.LogInformation("BizTalk-Aktion: {Count} offene(r) Auftrag/Auftraege fuer Server '{Server}'", pending.Count, serverId);

        foreach (var request in pending)
        {
            // The claim is taken before the Management API is called. It is created exclusively,
            // so a second worker running at the same time cannot execute the same request.
            var claim = await store.ClaimAsync(serverId, request.Id, WorkerId, cancellationToken);
            if (claim == null)
            {
                logger.LogInformation("BizTalk-Aktion: Auftrag '{Id}' wurde bereits von einem anderen Worker uebernommen", request.Id);
                continue;
            }

            var result = await ExecuteAsync(request, baseUrl, cancellationToken);
            results.Add(result);

            try
            {
                await store.SaveResultAsync(result, cancellationToken);
            }
            catch (Exception ex)
            {
                // The action was carried out, but the result could not be persisted. Repeating it
                // could stop an element that has meanwhile been started again, so the outcome is
                // marked as unclear and the request is never executed again.
                logger.LogError(ex, "BizTalk-Aktion: Ergebnis fuer Auftrag '{Id}' konnte nicht gespeichert werden", request.Id);
                await TryMarkUncertainAsync(serverId, request.Id, cancellationToken);
            }
        }

        return results;
    }

    /// <summary>Marks the claim as uncertain; a failure here must not abort the remaining actions.</summary>
    private async Task TryMarkUncertainAsync(string serverId, string requestId, CancellationToken cancellationToken)
    {
        try
        {
            await store.MarkClaimUncertainAsync(serverId, requestId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BizTalk-Aktion: Auftrag '{Id}' konnte nicht als unbestimmt markiert werden", requestId);
        }
    }

    /// <summary>Executes a single request and builds its result. Never throws.</summary>
    private async Task<BizTalkActionResult> ExecuteAsync(BizTalkActionRequest request, string baseUrl, CancellationToken cancellationToken)
    {
        var result = new BizTalkActionResult
        {
            RequestId = request.Id,
            ServerId = request.ServerId
        };

        try
        {
            var message = request.Artifact switch
            {
                BizTalkArtifactKind.Application => await StopApplicationAsync(request, baseUrl, cancellationToken),
                BizTalkArtifactKind.Orchestration => await StopOrchestrationAsync(request, baseUrl, cancellationToken),
                BizTalkArtifactKind.SendPort => await StopSendPortAsync(request, baseUrl, cancellationToken),
                BizTalkArtifactKind.ReceiveLocation => await DisableReceiveLocationAsync(request, baseUrl, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unbekannter Artefakt-Typ '{request.Artifact}' in Auftrag '{request.Id}'.")
            };

            result.State = BizTalkActionState.Succeeded;
            result.Message = message;
            logger.LogInformation("BizTalk-Aktion erfolgreich: {Message}", message);
        }
        catch (Exception ex)
        {
            result.State = BizTalkActionState.Failed;
            result.Message = $"{Describe(request)} konnte nicht deaktiviert werden.";
            result.Error = ex.Message;
            logger.LogWarning(ex, "BizTalk-Aktion fehlgeschlagen: {Artifact} '{Target}' (Auftrag {Id})",
                request.Artifact, request.TargetName, request.Id);
        }

        return result;
    }

    private async Task<string> StopApplicationAsync(BizTalkActionRequest request, string baseUrl, CancellationToken cancellationToken)
    {
        await apiClient.StopApplicationAsync(baseUrl, request.TargetName, cancellationToken);
        return $"BizTalk-Anwendung '{request.TargetName}' gestoppt.";
    }

    private async Task<string> StopOrchestrationAsync(BizTalkActionRequest request, string baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TargetParent))
            throw new InvalidOperationException(
                $"Für die Orchestrierung '{request.TargetName}' fehlt die zugehörige Anwendung.");

        await apiClient.StopOrchestrationAsync(baseUrl, request.TargetParent, request.TargetName, cancellationToken);
        return $"BizTalk-Orchestrierung '{request.TargetName}' gestoppt.";
    }

    private async Task<string> StopSendPortAsync(BizTalkActionRequest request, string baseUrl, CancellationToken cancellationToken)
    {
        await apiClient.StopSendPortAsync(baseUrl, request.TargetName, cancellationToken);
        return $"BizTalk-Sendeport '{request.TargetName}' gestoppt.";
    }

    private async Task<string> DisableReceiveLocationAsync(BizTalkActionRequest request, string baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TargetParent))
            throw new InvalidOperationException(
                $"Für den Empfangsort '{request.TargetName}' fehlt der zugehörige Empfangsport.");

        await apiClient.DisableReceiveLocationAsync(baseUrl, request.TargetParent, request.TargetName, cancellationToken);
        return $"BizTalk-Empfangsort '{request.TargetName}' deaktiviert.";
    }

    /// <summary>Short description of the requested action (for the result message).</summary>
    private static string Describe(BizTalkActionRequest request)
        => request.Artifact switch
        {
            BizTalkArtifactKind.Application => $"BizTalk-Anwendung '{request.TargetName}'",
            BizTalkArtifactKind.Orchestration => $"BizTalk-Orchestrierung '{request.TargetName}'",
            BizTalkArtifactKind.SendPort => $"BizTalk-Sendeport '{request.TargetName}'",
            BizTalkArtifactKind.ReceiveLocation => $"BizTalk-Empfangsort '{request.TargetName}'",
            _ => $"Artefakt '{request.TargetName}'"
        };
}
