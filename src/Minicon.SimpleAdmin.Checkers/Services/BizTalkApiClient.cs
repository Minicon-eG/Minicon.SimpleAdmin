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
