using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Thin IO seam over the BizTalk Management Service REST API. Only the read-only GET endpoints and
/// the few fields the monitoring needs are exposed. Failures propagate as exceptions (the checker
/// turns them into an "API unreachable" state). Kept behind an interface so the evaluation logic in
/// <see cref="BizTalkChecker"/> is unit-testable without HTTP.
/// </summary>
public interface IBizTalkApiClient
{
    Task<List<BtApplication>> GetApplicationsAsync(string baseUrl, CancellationToken cancellationToken = default);
    Task<List<BtOrchestration>> GetOrchestrationsAsync(string baseUrl, CancellationToken cancellationToken = default);
    Task<List<BtSendPort>> GetSendPortsAsync(string baseUrl, CancellationToken cancellationToken = default);
    Task<List<BtReceiveLocation>> GetReceiveLocationsAsync(string baseUrl, CancellationToken cancellationToken = default);
    Task<List<BtInstance>> GetInstancesAsync(string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>Stops an application (PUT .../Applications/{name}/Stop).</summary>
    Task StopApplicationAsync(string baseUrl, string applicationName, CancellationToken cancellationToken = default);

    /// <summary>Stops an orchestration (PUT .../Orchestrations/{applicationName}/{orchestrationName}/Stop).</summary>
    Task StopOrchestrationAsync(string baseUrl, string applicationName, string orchestrationName, CancellationToken cancellationToken = default);

    /// <summary>Stops a send port (PUT .../SendPorts/{name}/Stop).</summary>
    Task StopSendPortAsync(string baseUrl, string sendPortName, CancellationToken cancellationToken = default);

    /// <summary>Disables a receive location (PUT .../ReceiveLocations/Disable/{receivePortName}/{receiveLocationName}).</summary>
    Task DisableReceiveLocationAsync(string baseUrl, string receivePortName, string receiveLocationName, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BizTalkApiClient(HttpClient httpClient, ILogger<BizTalkApiClient> logger) : IBizTalkApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<List<BtApplication>> GetApplicationsAsync(string baseUrl, CancellationToken cancellationToken = default)
        => GetListAsync<BtApplication>(baseUrl, "/Applications", cancellationToken);

    public Task<List<BtOrchestration>> GetOrchestrationsAsync(string baseUrl, CancellationToken cancellationToken = default)
        => GetListAsync<BtOrchestration>(baseUrl, "/Orchestrations", cancellationToken);

    public Task<List<BtSendPort>> GetSendPortsAsync(string baseUrl, CancellationToken cancellationToken = default)
        => GetListAsync<BtSendPort>(baseUrl, "/SendPorts", cancellationToken);

    public Task<List<BtReceiveLocation>> GetReceiveLocationsAsync(string baseUrl, CancellationToken cancellationToken = default)
        => GetListAsync<BtReceiveLocation>(baseUrl, "/ReceiveLocations", cancellationToken);

    public Task<List<BtInstance>> GetInstancesAsync(string baseUrl, CancellationToken cancellationToken = default)
        => GetListAsync<BtInstance>(baseUrl, "/OperationalData/Instances", cancellationToken);

    private async Task<List<T>> GetListAsync<T>(string baseUrl, string path, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}{path}";
        logger.LogDebug("BizTalk: GET {Url}", url);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, cancellationToken);
        return list ?? new List<T>();
    }

    // --- write operations (deactivation actions) ---

    public Task StopApplicationAsync(string baseUrl, string applicationName, CancellationToken cancellationToken = default)
        => PutAsync(baseUrl, $"/Applications/{Uri.EscapeDataString(applicationName)}/Stop", null, cancellationToken);

    public Task StopOrchestrationAsync(string baseUrl, string applicationName, string orchestrationName, CancellationToken cancellationToken = default)
        => PutAsync(
            baseUrl,
            $"/Orchestrations/{Uri.EscapeDataString(applicationName)}/{Uri.EscapeDataString(orchestrationName)}/Stop",
            null,
            cancellationToken);

    public Task StopSendPortAsync(string baseUrl, string sendPortName, CancellationToken cancellationToken = default)
        => PutAsync(baseUrl, $"/SendPorts/{Uri.EscapeDataString(sendPortName)}/Stop", null, cancellationToken);

    public Task DisableReceiveLocationAsync(string baseUrl, string receivePortName, string receiveLocationName, CancellationToken cancellationToken = default)
        => PutAsync(
            baseUrl,
            $"/ReceiveLocations/Disable/{Uri.EscapeDataString(receivePortName)}/{Uri.EscapeDataString(receiveLocationName)}",
            null,
            cancellationToken);

    /// <summary>
    /// Issues a PUT against the Management API. The BizTalk endpoints answer 204 No Content on
    /// success; anything else is raised as an exception so the caller can report the failure.
    /// </summary>
    private async Task PutAsync(string baseUrl, string path, object? body, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}{path}";
        logger.LogInformation("BizTalk: PUT {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        if (body != null)
            request.Content = JsonContent.Create(body);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new BizTalkApiException(
                $"BizTalk-API antwortete mit {(int)response.StatusCode} {response.ReasonPhrase} für {path}: {detail}");
        }
    }
}

/// <summary>Raised when a write operation against the BizTalk Management API fails.</summary>
public sealed class BizTalkApiException : Exception
{
    public BizTalkApiException(string message) : base(message)
    {
    }

    public BizTalkApiException(string message, Exception inner) : base(message, inner)
    {
    }
}

// --- DTOs (subset of the Management API models needed for monitoring) ---

public sealed class BtApplication
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public bool IsSystem { get; set; }
}

public sealed class BtOrchestration
{
    public string? FullName { get; set; }
    public string? ApplicationName { get; set; }
    public string? Status { get; set; }
}

public sealed class BtSendPort
{
    public string? Name { get; set; }
    public string? ApplicationName { get; set; }
    public string? Status { get; set; }
}

public sealed class BtReceiveLocation
{
    public string? Name { get; set; }
    public string? ReceivePortName { get; set; }
    public bool Enable { get; set; }
}

public sealed class BtInstance
{
    public string? InstanceStatus { get; set; }
    public string? Application { get; set; }
    public string? HostName { get; set; }
    public string? ServiceType { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
}
