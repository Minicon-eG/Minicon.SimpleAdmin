using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Reads aggregated status, acknowledges and peer notify-state over HTTP for the central notifier.
/// All methods return null / empty on failure (network error, timeout, malformed JSON) and log a
/// warning — a single unreachable server must never abort the whole notifier cycle.
/// </summary>
public interface IHttpStatusReader
{
    Task<CentralServerStatus?> FetchServerStatusAsync(string url, CancellationToken cancellationToken = default);
    Task<List<Acknowledge>> FetchActiveAcknowledgesAsync(string url, CancellationToken cancellationToken = default);
    Task<NotifyState?> FetchNotifyStateAsync(string url, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class HttpStatusReader(HttpClient httpClient, ILogger<HttpStatusReader> logger) : IHttpStatusReader
{
    // JsonStringEnumConverter reads both string- and number-encoded enums, so this reader is
    // tolerant of however the publishing side serialised ServiceStatus/MetricStatus/etc.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<CentralServerStatus?> FetchServerStatusAsync(string url, CancellationToken cancellationToken = default)
        => await GetJsonAsync<CentralServerStatus>(url, "server status", cancellationToken);

    public async Task<List<Acknowledge>> FetchActiveAcknowledgesAsync(string url, CancellationToken cancellationToken = default)
    {
        var state = await GetJsonAsync<AcknowledgeState>(url, "acknowledges", cancellationToken);
        if (state == null)
            return new List<Acknowledge>();

        return state.Acknowledges
            .Where(a => a.Status == AcknowledgeStatus.Active)
            .ToList();
    }

    public async Task<NotifyState?> FetchNotifyStateAsync(string url, CancellationToken cancellationToken = default)
        => await GetJsonAsync<NotifyState>(url, "notify-state", cancellationToken);

    private async Task<T?> GetJsonAsync<T>(string url, string what, CancellationToken cancellationToken) where T : class
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Notifier: HTTP {Status} fetching {What} from {Url}", (int)response.StatusCode, what, url);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Notifier: failed to fetch {What} from {Url}", what, url);
            return null;
        }
    }
}
