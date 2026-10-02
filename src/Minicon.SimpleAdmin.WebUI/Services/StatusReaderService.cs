using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Services;

/// <summary>
/// Reads RuntimeStatus files from the status directory and aggregates per server
/// </summary>
public class StatusReaderService
{
    private readonly string _defaultStatusDirectory;
    private readonly IAcknowledgeService _acknowledgeService;
    private readonly ConfigurationService _configService;
    private readonly ILogger<StatusReaderService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _effectiveStatusDirectory;

    private List<RuntimeStatus>? _cachedStatuses;
    private DateTime _lastLoad = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(10);
    private readonly HttpClient? _httpClient;

    /// <summary>Service type used for the placeholder entry of a server whose status could not be retrieved.</summary>
    internal const string UnreachableServiceType = "erreichbarkeit";

    public StatusReaderService(
        string defaultStatusDirectory,
        IAcknowledgeService acknowledgeService,
        ConfigurationService configService,
        ILogger<StatusReaderService> logger,
        HttpClient? httpClient = null)
    {
        _defaultStatusDirectory = defaultStatusDirectory;
        _httpClient = httpClient;
        _acknowledgeService = acknowledgeService;
        _configService = configService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        if (!Directory.Exists(_defaultStatusDirectory))
        {
            _logger.LogWarning("Default status directory does not exist: {StatusDirectory}", _defaultStatusDirectory);
        }
    }

    /// <summary>
    /// Gets all server status views aggregated from RuntimeStatus files
    /// </summary>
    public async Task<List<ServerStatusView>> GetAllServersAsync(bool forceRefresh = false)
    {
        var statuses = await LoadAllStatusesAsync(forceRefresh);
        var activeAcknowledges = await LoadActiveAcknowledgesAsync();
        return AggregateByServer(statuses, activeAcknowledges);
    }

    /// <summary>
    /// Gets a single server's aggregated status view
    /// </summary>
    public async Task<ServerStatusView?> GetServerAsync(string serverId, bool forceRefresh = false)
    {
        var servers = await GetAllServersAsync(forceRefresh);
        return servers.FirstOrDefault(s => s.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all active problems across all servers (derived from metrics + AppPools)
    /// </summary>
    public async Task<List<(string ServerId, ActiveProblem Problem)>> GetAllProblemsAsync(bool forceRefresh = false)
    {
        var servers = await GetAllServersAsync(forceRefresh);
        var problems = new List<(string ServerId, ActiveProblem Problem)>();

        foreach (var server in servers)
        {
            foreach (var problem in server.ActiveProblems)
            {
                problems.Add((server.ServerId, problem));
            }
        }

        return problems.OrderByDescending(p => p.Problem.Severity)
            .ThenByDescending(p => p.Problem.LastOccurrence)
            .ToList();
    }

    /// <summary>
    /// Gets problems for a specific server
    /// </summary>
    public async Task<List<(string ServerId, ActiveProblem Problem)>> GetProblemsForServerAsync(string serverId, bool forceRefresh = false)
    {
        var server = await GetServerAsync(serverId, forceRefresh);
        if (server == null) return new();
        return server.ActiveProblems.Select(p => (server.ServerId, p)).ToList();
    }

    /// <summary>
    /// Invalidates the cache
    /// </summary>
    public void InvalidateCache()
    {
        _cachedStatuses = null;
        _lastLoad = DateTime.MinValue;
    }

    private async Task<List<RuntimeStatus>> LoadAllStatusesAsync(bool forceRefresh = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedStatuses != null && DateTime.UtcNow - _lastLoad < _cacheExpiry)
            {
                return _cachedStatuses;
            }

            var statusDirectory = GetEffectiveStatusDirectory();
            var statuses = await LoadStatusesFromConfiguredLocationAsync(statusDirectory);

            _logger.LogDebug("Loaded {Count} status files", statuses.Count);
            _cachedStatuses = statuses;
            _lastLoad = DateTime.UtcNow;
            return statuses;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Merges every status source in the directory: single-status files (<c>&lt;server&gt;.&lt;type&gt;.json</c>)
    /// directly in it and in its <c>status</c> subfolder, plus per-server combined files
    /// (<c>&lt;host&gt;.status.json</c>, written via <c>output.centralOutputPath</c>). Per server/service the
    /// newest entry wins; on a tie the single-status file is kept (it carries the history). This way a
    /// central host whose local worker writes into the same folder as the other servers still shows all
    /// servers — no separate directories needed.
    /// </summary>
    private async Task<List<RuntimeStatus>> LoadStatusesFromConfiguredLocationAsync(string statusDirectory)
    {
        var merged = new Dictionary<string, RuntimeStatus>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<RuntimeStatus> statuses)
        {
            foreach (var status in statuses)
            {
                var key = $"{status.Server}|{status.Service}";
                if (!merged.TryGetValue(key, out var existing) || status.LastUpdate > existing.LastUpdate)
                    merged[key] = status;
            }
        }

        foreach (var runtimeDirectory in GetRuntimeStatusDirectories(statusDirectory))
            Add(await LoadRuntimeStatusesAsync(runtimeDirectory));

        Add(await LoadCentralServerStatusesAsync(statusDirectory));

        if (_httpClient != null && _configService.IsLoaded && _configService.GetOutputSettings().PullStatusOverHttp)
        {
            var servers = _configService.GetServers().Where(s => s.Active && !string.IsNullOrWhiteSpace(s.BaseUrl)).ToList();
            var results = await Task.WhenAll(servers.Select(async s => (Server: s, Status: await FetchServerStatusAsync(s.BaseUrl!, s.Name))));

            foreach (var (_, status) in results.Where(r => r.Status != null))
                Add(status!.Statuses.Where(IsValidRuntimeStatus));

            // A configured server with neither HTTP nor file data is shown as unreachable instead of silently missing.
            foreach (var (server, _) in results.Where(r => r.Status == null))
            {
                if (merged.Values.Any(m => string.Equals(m.Server, server.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                merged[$"{server.Name}|{UnreachableServiceType}"] = new RuntimeStatus
                {
                    Server = server.Name,
                    Service = UnreachableServiceType,
                    Status = ServiceStatus.Unknown,
                    StatusMessage = $"Status nicht abrufbar: {server.BaseUrl!.TrimEnd('/')}/status/{server.Name}.status.json"
                };
            }
        }

        // Event log ignore rules from the central config apply to every server shown here.
        if (_configService.IsLoaded)
        {
            var eventLogConfig = _configService.GetFeatures().EventLog;
            var now = DateTime.UtcNow;
            foreach (var status in merged.Values)
                EventLogIgnoreMatcher.ApplyCentral(status.EventLogs, status.Server, eventLogConfig.IgnoreRules, eventLogConfig.Defaults, now);
        }

        return merged.Values.ToList();
    }

    private async Task<CentralServerStatus?> FetchServerStatusAsync(string baseUrl, string serverName)
    {
        var url = $"{baseUrl.TrimEnd('/')}/status/{serverName}.status.json";
        try
        {
            using var response = await _httpClient!.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Status fetch {Url} returned {StatusCode}", url, (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<CentralServerStatus>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Status fetch {Url} failed: {Error}", url, ex.Message);
            return null;
        }
    }

    private static IEnumerable<string> GetRuntimeStatusDirectories(string statusDirectory)
    {
        yield return statusDirectory;

        var nestedStatusDirectory = Path.Combine(statusDirectory, "status");
        if (!string.Equals(nestedStatusDirectory, statusDirectory, StringComparison.OrdinalIgnoreCase))
        {
            yield return nestedStatusDirectory;
        }
    }

    private async Task<List<RuntimeStatus>> LoadRuntimeStatusesAsync(string statusDirectory)
    {
        var statuses = new List<RuntimeStatus>();
        if (!Directory.Exists(statusDirectory))
        {
            return statuses;
        }

        var files = Directory.GetFiles(statusDirectory, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("acknowledges.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var status = JsonSerializer.Deserialize<RuntimeStatus>(json, _jsonOptions);
                if (IsValidRuntimeStatus(status))
                {
                    statuses.Add(status!);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping invalid status file {File} (not a RuntimeStatus)", file);
            }
        }

        return statuses;
    }

    private async Task<List<RuntimeStatus>> LoadCentralServerStatusesAsync(string statusDirectory)
    {
        var statuses = new List<RuntimeStatus>();
        if (!Directory.Exists(statusDirectory))
        {
            _logger.LogDebug("Status directory not found, returning empty list");
            return statuses;
        }

        var files = Directory.GetFiles(statusDirectory, "*.status.json");
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var centralStatus = JsonSerializer.Deserialize<CentralServerStatus>(json, _jsonOptions);
                if (centralStatus?.Statuses == null)
                {
                    continue;
                }

                statuses.AddRange(centralStatus.Statuses.Where(IsValidRuntimeStatus)!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading central server status file {File}", file);
            }
        }

        return statuses;
    }

    private static bool IsValidRuntimeStatus(RuntimeStatus? status)
    {
        return status != null &&
            !string.IsNullOrWhiteSpace(status.Server) &&
            !string.IsNullOrWhiteSpace(status.Service);
    }

    private async Task<List<Acknowledge>> LoadActiveAcknowledgesAsync()
    {
        var acknowledgeState = await _acknowledgeService.LoadAcknowledgesAsync();
        var now = DateTime.UtcNow;
        return acknowledgeState.Acknowledges
            .Where(a => a.IsEffective(now))
            .ToList();
    }

    private List<ServerStatusView> AggregateByServer(
        List<RuntimeStatus> statuses,
        IReadOnlyCollection<Acknowledge> activeAcknowledges)
    {
        _logger.LogInformation("AggregateByServer: Processing {ServerCount} status files", statuses.Count);

        return statuses
            .GroupBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                _logger.LogInformation("Aggregating server: {Server}", g.Key);
                var services = g.OrderBy(s => s.Service).ToList();
                _logger.LogInformation("  Service count: {ServiceCount}", services.Count);

                foreach (var service in services)
                {
                    service.Status = CalculateServiceStatus(service, activeAcknowledges);
                }

                var newest = services.OrderByDescending(s => s.LastUpdate).First();

                // Use metrics from the newest service status
                var metrics = newest.Metrics;

                // AppPools from any service (they're server-wide, same on all)
                var appPools = services.FirstOrDefault(s => s.AppPools != null && s.AppPools.Count > 0)?.AppPools;
                if (appPools != null)
                    _logger.LogInformation("  AppPools found: {PoolCount}", appPools.Count);

                // Windows service states (server-wide, same on all)
                var windowsServices = services.FirstOrDefault(s => s.Services != null && s.Services.Count > 0)?.Services;
                if (windowsServices != null)
                    _logger.LogInformation("  Windows Services found: {ServiceCount}", windowsServices.Count);

                // Event Log state (server-wide, same on all)
                var eventLogs = services.FirstOrDefault(s => s.EventLogs != null)?.EventLogs;
                if (eventLogs != null)
                    _logger.LogInformation("  Event Logs found: {EventCount} events, Status: {Status}", eventLogs.EventCount, eventLogs.Status);

                // SQL Query Check states (server-wide, same on all services that run them)
                var sqlQueryChecks = services
                    .Where(s => s.SqlQueryChecks != null && s.SqlQueryChecks.Count > 0)
                    .SelectMany(s => s.SqlQueryChecks!)
                    .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

                if (sqlQueryChecks.Count > 0)
                {
                    _logger.LogInformation("  SQL Query Checks found: {CheckCount}", sqlQueryChecks.Count);
                    foreach (var (checkId, checkState) in sqlQueryChecks)
                    {
                        _logger.LogInformation("    Check '{CheckId}': Name={CheckName}, Status={Status}, Results={ResultCount}, ConnectionError={HasError}",
                            checkId, checkState.CheckName, checkState.Status, checkState.Results?.Count ?? 0,
                            !string.IsNullOrEmpty(checkState.ConnectionError));
                    }
                }
                else
                {
                    _logger.LogInformation("  No SQL Query Checks found in status files");
                    foreach (var service in services)
                    {
                        _logger.LogDebug("    Service {Service}: SqlQueryChecks={HasSqlChecks}, Count={Count}",
                            service.Service,
                            service.SqlQueryChecks != null ? "yes" : "no",
                            service.SqlQueryChecks?.Count ?? 0);
                    }
                }

                // Email Probe states (sender role — server-wide)
                var emailProbes = services
                    .Where(s => s.EmailProbes != null && s.EmailProbes.Count > 0)
                    .SelectMany(s => s.EmailProbes!)
                    .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(gr => gr.Key, gr => gr.First().Value, StringComparer.OrdinalIgnoreCase);

                // Email Delivery states (checker role — server-wide)
                var emailDelivery = services
                    .Where(s => s.EmailDelivery != null && s.EmailDelivery.Count > 0)
                    .SelectMany(s => s.EmailDelivery!)
                    .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(gr => gr.Key, gr => gr.First().Value, StringComparer.OrdinalIgnoreCase);

                // Certificate states (server-wide)
                var certificates = services
                    .Where(s => s.Certificates != null && s.Certificates.Count > 0)
                    .SelectMany(s => s.Certificates!)
                    .GroupBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(gr => gr.Key, gr => gr.First().Value, StringComparer.OrdinalIgnoreCase);

                // BizTalk state (server-wide; single object per status — take the first present)
                var bizTalk = services.Select(s => s.BizTalk).FirstOrDefault(b => b != null);

                // File monitoring state (server-wide; single object per status — take the first present)
                var fileMonitoring = services.Select(s => s.FileMonitoring).FirstOrDefault(f => f != null);

                // BaseUrl from config for static HTML links
                var serverConfig = _configService.IsLoaded
                    ? _configService.Config.Servers.FirstOrDefault(s =>
                        s.GetEffectiveId().Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                    : null;
                var baseUrl = serverConfig?.BaseUrl;
                if (baseUrl != null)
                    _logger.LogInformation("  BaseUrl: {BaseUrl}", baseUrl);

                // Derive problems from metrics + AppPools + Windows services + EventLogs + SQL Checks + Email
                var problems = ProblemDerivationService.DeriveProblems(
                    metrics,
                    appPools,
                    windowsServices,
                    eventLogs,
                    sqlQueryChecks.Count > 0 ? sqlQueryChecks : null,
                    activeAcknowledges,
                    g.Key,
                    emailProbes.Count > 0 ? emailProbes : null,
                    emailDelivery.Count > 0 ? emailDelivery : null,
                    certificates.Count > 0 ? certificates : null,
                    bizTalk,
                    fileMonitoring);

                // Calculate overall status
                var overallStatus = CalculateOverallStatus(services, problems);

                _logger.LogInformation("  Creating ServerStatusView: OverallStatus={OverallStatus}, SqlQueryChecks={SqlCheckCount}, Problems={ProblemCount}",
                    overallStatus, sqlQueryChecks.Count, problems.Count);

                return new ServerStatusView
                {
                    ServerId = g.Key,
                    LastCheck = newest.LastUpdate,
                    OverallStatus = overallStatus,
                    Metrics = metrics,
                    AppPools = appPools,
                    WindowsServices = windowsServices,
                    EventLogs = eventLogs,
                    SqlQueryChecks = sqlQueryChecks,
                    EmailProbes = emailProbes.Count > 0 ? emailProbes : null,
                    EmailDelivery = emailDelivery.Count > 0 ? emailDelivery : null,
                    Certificates = certificates.Count > 0 ? certificates : null,
                    BizTalk = bizTalk,
                    FileMonitoring = fileMonitoring,
                    ActiveProblems = problems,
                    Services = services,
                    BaseUrl = baseUrl
                };
            })
            .OrderBy(s => s.ServerId)
            .ToList();
    }

    private static ServiceStatus CalculateOverallStatus(
        List<RuntimeStatus> services,
        List<ActiveProblem> activeProblems)
    {
        if (services.Any(s => s.Status == ServiceStatus.Unhealthy) ||
            activeProblems.Any(p => !p.Acknowledged && p.Severity == ProblemSeverity.Critical))
            return ServiceStatus.Unhealthy;

        if (services.Any(s => s.Status == ServiceStatus.Degraded) ||
            activeProblems.Any(p => !p.Acknowledged && p.Severity == ProblemSeverity.Warning))
            return ServiceStatus.Degraded;

        if (services.Any(s => s.Status == ServiceStatus.Unreachable))
            return ServiceStatus.Unreachable;

        if (services.Any(s => s.Status == ServiceStatus.Acknowledged) ||
            (activeProblems.Count > 0 && activeProblems.All(p => p.Acknowledged)))
        {
            return ServiceStatus.Acknowledged;
        }

        if (services.All(s => s.Status == ServiceStatus.Healthy))
            return ServiceStatus.Healthy;

        if (services.All(s => s.Status == ServiceStatus.Unknown))
            return ServiceStatus.Unknown;

        return ServiceStatus.Healthy;
    }

    private static ServiceStatus CalculateServiceStatus(
        RuntimeStatus service,
        IReadOnlyCollection<Acknowledge> activeAcknowledges)
    {
        if (service.Status == ServiceStatus.Unreachable)
        {
            return ServiceStatus.Unreachable;
        }

        var problems = ProblemDerivationService.DeriveProblems(
            service.Metrics,
            service.AppPools,
            service.Services,
            service.EventLogs,
            service.SqlQueryChecks,
            activeAcknowledges,
            service.Server,
            service.EmailProbes,
            service.EmailDelivery,
            service.Certificates,
            service.BizTalk,
            service.FileMonitoring);

        if (problems.Count > 0)
        {
            if (problems.All(p => p.Acknowledged))
            {
                return ServiceStatus.Acknowledged;
            }

            if (problems.Any(p => !p.Acknowledged && p.Severity == ProblemSeverity.Critical))
            {
                return ServiceStatus.Unhealthy;
            }

            if (problems.Any(p => !p.Acknowledged && p.Severity == ProblemSeverity.Warning))
            {
                return ServiceStatus.Degraded;
            }
        }

        if (service.Metrics.Count == 0)
        {
            // No PRTG metrics configured — if any monitoring checks are present and all passed
            // (no problems derived above), report Healthy rather than Unknown
            var hasCheckData = service.Services?.Count > 0
                || service.AppPools?.Count > 0
                || service.SqlQueryChecks?.Count > 0
                || service.EmailProbes?.Count > 0
                || service.EmailDelivery?.Count > 0
                || service.Certificates?.Count > 0
                || service.BizTalk != null
                || service.FileMonitoring != null;
            return hasCheckData ? ServiceStatus.Healthy : ServiceStatus.Unknown;
        }

        if (service.Metrics.Any(m => m.Status == Minicon.SimpleAdmin.Models.Status.MetricStatus.Critical))
        {
            return ServiceStatus.Unhealthy;
        }

        if (service.Metrics.Any(m => m.Status == Minicon.SimpleAdmin.Models.Status.MetricStatus.Warning))
        {
            return ServiceStatus.Degraded;
        }

        return ServiceStatus.Healthy;
    }

    private string GetEffectiveStatusDirectory()
    {
        // Use the configured CentralOutputPath or default status directory
        var configuredPath = _configService.IsLoaded
            ? _configService.GetOutputSettings().CentralOutputPath
            : null;

        var selectedPath = string.IsNullOrWhiteSpace(configuredPath)
            ? _defaultStatusDirectory
            : configuredPath.Trim();

        if (string.Equals(_effectiveStatusDirectory, selectedPath, StringComparison.Ordinal))
        {
            return selectedPath;
        }

        _effectiveStatusDirectory = selectedPath;
        InvalidateCache();
        _logger.LogInformation("Status directory changed to: {StatusDirectory}", _effectiveStatusDirectory);

        return selectedPath;
    }
}
