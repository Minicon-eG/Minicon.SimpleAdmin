using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.HtmlGenerator;
using System.Diagnostics;
using MetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Worker;

/// <summary>
/// Runtime options passed into the worker host. Allows the host bootstrapper to inject
/// behaviour (e.g. forcing email probes regardless of rate limit) without
/// changing the worker logic.
/// </summary>
public sealed class RuntimeOptions
{
    public bool ForceProbe { get; init; }
}

/// <summary>
/// Worker class that orchestrates the status generation process:
/// loads config, runs all enabled checkers in parallel, derives problems,
/// evaluates status, persists runtime status, and generates static HTML.
/// </summary>
public class SimpleAdminWorker
{
    private readonly IServiceConfigReader _configReader;
    private readonly IMetricsCollector _metricsCollector;
    private readonly IRuntimeStatusStore _statusStore;
    private readonly IStaticHtmlGenerator _htmlGenerator;
    private readonly IStatusEvaluator _statusEvaluator;
    private readonly IAcknowledgeService _acknowledgeService;
    private readonly IAppPoolChecker _appPoolChecker;
    private readonly IWindowsServiceChecker _serviceChecker;
    private readonly IEventLogChecker _eventLogChecker;
    private readonly ICertificateChecker _certificateChecker;
    private readonly IBizTalkChecker _bizTalkChecker;
    private readonly IFileMonitoringChecker _fileMonitoringChecker;
    private readonly SqlQueryChecker _sqlQueryChecker;
    private readonly IEmailProbeSender _emailProbeSender;
    private readonly IEmailDeliveryChecker _emailDeliveryChecker;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;
    private readonly RuntimeOptions _runtimeOptions;
    private readonly ILogger<SimpleAdminWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SimpleAdminWorker(
        IServiceConfigReader configReader,
        IMetricsCollector metricsCollector,
        IRuntimeStatusStore statusStore,
        IStaticHtmlGenerator htmlGenerator,
        IStatusEvaluator statusEvaluator,
        IAcknowledgeService acknowledgeService,
        IAppPoolChecker appPoolChecker,
        IWindowsServiceChecker serviceChecker,
        IEventLogChecker eventLogChecker,
        ICertificateChecker certificateChecker,
        IBizTalkChecker bizTalkChecker,
        IFileMonitoringChecker fileMonitoringChecker,
        SqlQueryChecker sqlQueryChecker,
        IEmailProbeSender emailProbeSender,
        IEmailDeliveryChecker emailDeliveryChecker,
        INotificationService notificationService,
        IConfiguration configuration,
        RuntimeOptions runtimeOptions,
        ILogger<SimpleAdminWorker> logger,
        ILoggerFactory loggerFactory)
    {
        _configReader = configReader;
        _metricsCollector = metricsCollector;
        _statusStore = statusStore;
        _htmlGenerator = htmlGenerator;
        _statusEvaluator = statusEvaluator;
        _acknowledgeService = acknowledgeService;
        _appPoolChecker = appPoolChecker;
        _serviceChecker = serviceChecker;
        _eventLogChecker = eventLogChecker;
        _certificateChecker = certificateChecker;
        _bizTalkChecker = bizTalkChecker;
        _fileMonitoringChecker = fileMonitoringChecker;
        _sqlQueryChecker = sqlQueryChecker;
        _emailProbeSender = emailProbeSender;
        _emailDeliveryChecker = emailDeliveryChecker;
        _notificationService = notificationService;
        _configuration = configuration;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task RunAsync()
    {
        // Load configuration
        var config = await _configReader.LoadConfigAsync();

        var maxHistoryEntries = config.History?.MaxEntries ?? 500;
        var minConsecutiveChecks = config.History?.MinConsecutiveChecks ?? 5;
        _logger.LogInformation("History limit: {MaxHistoryEntries} entries, Min consecutive checks: {MinConsecutiveChecks}",
            maxHistoryEntries, minConsecutiveChecks);

        // Get current machine hostname
        var currentHostname = Environment.MachineName;
        _logger.LogInformation("Current hostname: {Hostname}", currentHostname);

        // Find server matching current hostname (case-insensitive)
        var currentServer = config.Servers
            .Where(s => s.Active)
            .FirstOrDefault(s => s.Name.Equals(currentHostname, StringComparison.OrdinalIgnoreCase));

        if (currentServer == null)
        {
            _logger.LogWarning("No active server configuration found for hostname '{Hostname}'. Available servers: {Servers}",
                currentHostname,
                string.Join(", ", config.Servers.Where(s => s.Active).Select(s => s.Name)));
            // This host may still be a dedicated notifier (config-driven role).
            await MaybeRunNotifierCycleAsync(config, currentHostname);
            return;
        }

        // Process expired acknowledges
        var expiredAcks = await _acknowledgeService.ProcessExpiredAcknowledgesAsync();
        if (expiredAcks.Count > 0)
        {
            _logger.LogInformation("Processed {Count} expired acknowledges", expiredAcks.Count);
        }

        // Check if features are enabled
        var features = config.Features ?? new FeaturesConfig();
        _logger.LogDebug("Features - SystemMetrics: {SystemMetrics}, AppPools: {AppPools}, EventLog: {EventLog}, Acknowledge: {Acknowledge}",
            features.SystemMetrics.Enabled, features.AppPools.Enabled, features.EventLog.Enabled, features.Acknowledge.Enabled);

        var activeAcknowledges = features.Acknowledge.Enabled
            ? (await _acknowledgeService.LoadAcknowledgesAsync())
                .Acknowledges
                .Where(a => a.Status == AcknowledgeStatus.Active)
                .ToList()
            : new List<Acknowledge>();

        _logger.LogInformation("Processing server: {ServerName} ({Description})",
            currentServer.Name, currentServer.Description ?? "No description");

        // Encryption key used by email and SQL checkers that need credential decryption
        var encryptionKey = _configuration["Encryption:ConnectionStringKey"] ?? "";

        // Run all optional checkers in parallel
        _logger.LogInformation("  - Running checkers in parallel (Metrics, AppPools, Services, EventLog, SqlQueries, EmailProbes, EmailDelivery)");

        // Load previous status from the first service type's JSON for state carry-forward
        RuntimeStatus? previousStatus = null;
        if (currentServer.ServiceTypes.Count > 0)
        {
            previousStatus = await _statusStore.LoadStatusAsync(currentServer.Name, currentServer.ServiceTypes[0].Type);
        }

        // Load previous AppPool states so StoppedSince is preserved across runs
        Dictionary<string, AppPoolState>? previousAppPoolStates = null;
        if (features.AppPools.Enabled && currentServer.Checks?.AppPools?.Enabled == true)
        {
            previousAppPoolStates = previousStatus?.AppPools;
        }

        // Load previous email states for ConsecutiveFailures carry-forward (status smoothing)
        var previousEmailProbeStates = previousStatus?.EmailProbes;
        var previousEmailDeliveryStates = previousStatus?.EmailDelivery;

        var overallSw = Stopwatch.StartNew();

        var appPoolTask = features.AppPools.Enabled && currentServer.Checks?.AppPools?.Enabled == true
            ? TrackAsync("AppPools", () => _appPoolChecker.CheckAppPoolsAsync(currentServer.Checks.AppPools, features.AppPools.Defaults, previousAppPoolStates))
            : Task.FromResult<Dictionary<string, AppPoolState>?>(null);

        var serviceTask = features.Services.Enabled && currentServer.Checks?.Services?.Enabled == true && currentServer.Checks.Services.Checks.Count > 0
            ? TrackAsync("Services", () => _serviceChecker.CheckServicesAsync(currentServer.Checks.Services.Checks, features.Services.Enabled))
            : Task.FromResult<Dictionary<string, ServiceState>?>(null);

        var eventLogConfig = currentServer.Checks?.EventLog?.Enabled == true
            ? (currentServer.Checks.EventLog.Logs.Count > 0
                ? currentServer.Checks.EventLog
                : new EventLogChecksConfig { Enabled = true })
            : null;
        // Central ignore rules: pre-filter by server scope (empty servers list = all servers)
        var eventLogIgnoreRules = features.EventLog.IgnoreRules
            .Where(r => EventLogChecker.RuleAppliesToServer(r, currentHostname))
            .ToList();
        var eventLogTask = features.EventLog.Enabled && eventLogConfig != null
            ? TrackAsync("EventLog", () => _eventLogChecker.CheckEventLogsAsync(eventLogConfig, features.EventLog.Defaults, eventLogIgnoreRules))
            : Task.FromResult<EventLogState?>(null);

        var sqlCfg = currentServer.Checks?.SqlQueries;
        var sqlTask = features.SqlQueries.Enabled && sqlCfg?.Enabled == true && sqlCfg.Checks.Count > 0
            ? TrackAsync("SqlQueries", () => _sqlQueryChecker.CheckAllAsync(sqlCfg.Checks, features.SqlQueries.DefaultTimeoutSeconds))
            : Task.FromResult<Dictionary<string, SqlQueryCheckState>?>(null);

        var emailProbesCfg = currentServer.Checks?.EmailProbes;
        var emailProbeTask = emailProbesCfg?.Enabled == true && emailProbesCfg.Probes.Count > 0
            ? TrackAsync("EmailProbes", () => _emailProbeSender.SendPendingProbesAsync(emailProbesCfg.Probes, encryptionKey, previousEmailProbeStates, _runtimeOptions.ForceProbe))
            : Task.FromResult<Dictionary<string, EmailProbeState>?>(null);

        var emailDeliveryCfg = currentServer.Checks?.EmailDelivery;
        var emailDeliveryTask = emailDeliveryCfg?.Enabled == true && emailDeliveryCfg.Checks.Count > 0
            ? TrackAsync("EmailDelivery", () => _emailDeliveryChecker.CheckDeliveryAsync(emailDeliveryCfg, encryptionKey, previousEmailDeliveryStates))
            : Task.FromResult<Dictionary<string, EmailDeliveryCheckState>?>(null);

        var certCfg = currentServer.Checks?.Certificates;
        var certificateTask = features.Certificates.Enabled && certCfg?.Enabled == true && certCfg.Checks.Count > 0
            ? TrackAsync("Certificates", () => _certificateChecker.CheckCertificatesAsync(certCfg, features.Certificates.Defaults))
            : Task.FromResult<Dictionary<string, CertificateState>?>(null);

        var biztalkCfg = currentServer.Checks?.BizTalk;
        var bizTalkTask = features.BizTalk.Enabled && biztalkCfg?.Enabled == true
            ? TrackAsync("BizTalk", () => _bizTalkChecker.CheckAsync(biztalkCfg, features.BizTalk.Defaults))!
            : Task.FromResult<BizTalkState?>(null);

        var fileMonCfg = currentServer.Checks?.FileMonitoring;
        var fileMonitoringTask = features.FileMonitoring.Enabled && fileMonCfg?.Enabled == true
            && (fileMonCfg.Directories.Count > 0 || fileMonCfg.LogScans.Count > 0)
            ? TrackAsync("FileMonitoring", () => _fileMonitoringChecker.CheckAsync(fileMonCfg, features.FileMonitoring.Defaults))!
            : Task.FromResult<FileMonitoringState?>(null);

        // Collect metrics for all ServiceTypes in parallel to the other checkers.
        // Within this task, ServiceTypes are processed sequentially because the platform
        // metrics providers (e.g. WindowsMetricsProvider) share counter instances and are
        // not safe to call concurrently.
        var metricsTask = TrackAsync("Metrics", () => Task.Run(async () =>
        {
            var metricsLogger = _loggerFactory.CreateLogger("Minicon.SimpleAdmin.Checkers.Metrics");
            var result = new Dictionary<string, List<Metric>>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in currentServer.ServiceTypes)
            {
                metricsLogger.LogDebug("[Metrics] collecting for service type '{ServiceType}'", st.Type);
                try
                {
                    result[st.Type] = await _metricsCollector.CollectMetricsAsync(st.Prtg.Checks);
                }
                catch (Exception ex)
                {
                    metricsLogger.LogError(ex, "Error collecting metrics for {ServiceType}", st.Type);
                    result[st.Type] = new List<Metric>();
                }
            }
            return result;
        }));

        try
        {
            await Task.WhenAll(appPoolTask, serviceTask, eventLogTask, sqlTask, emailProbeTask, emailDeliveryTask, certificateTask, bizTalkTask, fileMonitoringTask, metricsTask);
        }
        catch
        {
            // Individual task exceptions are handled below
        }
        _logger.LogDebug("All parallel checkers finished in {ElapsedMs}ms total", overallSw.ElapsedMilliseconds);

        Dictionary<string, AppPoolState>? appPoolStates = null;
        try { appPoolStates = await appPoolTask; _logger.LogInformation("    AppPools: {Count}", appPoolStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error checking AppPools"); }

        Dictionary<string, ServiceState>? serviceStates = null;
        try { serviceStates = await serviceTask; _logger.LogInformation("    Services: {Count}", serviceStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error checking Windows Services"); }

        EventLogState? eventLogState = null;
        try
        {
            eventLogState = await eventLogTask;
            if (eventLogState != null)
                _logger.LogInformation("    EventLog: {Count} events, Status: {Status}", eventLogState.EventCount, eventLogState.Status);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error checking Event Log"); }

        Dictionary<string, SqlQueryCheckState>? sqlQueryStates = null;
        try { sqlQueryStates = await sqlTask; _logger.LogInformation("    SqlQueries: {Count} check(s)", sqlQueryStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error running SQL query checks"); }

        Dictionary<string, EmailProbeState>? emailProbeStates = null;
        try { emailProbeStates = await emailProbeTask; _logger.LogInformation("    EmailProbes: {Count} probe(s)", emailProbeStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error sending email probes"); }

        Dictionary<string, EmailDeliveryCheckState>? emailDeliveryStates = null;
        try { emailDeliveryStates = await emailDeliveryTask; _logger.LogInformation("    EmailDelivery: {Count} check(s)", emailDeliveryStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error checking email delivery"); }

        Dictionary<string, CertificateState>? certificateStates = null;
        try { certificateStates = await certificateTask; _logger.LogInformation("    Certificates: {Count}", certificateStates?.Count ?? 0); }
        catch (Exception ex) { _logger.LogError(ex, "Error checking certificates"); }

        BizTalkState? bizTalkState = null;
        try
        {
            bizTalkState = await bizTalkTask;
            if (bizTalkState != null)
                _logger.LogInformation("    BizTalk: reachable={Reachable}, {Problems} problem(s), {Suspended} suspended",
                    bizTalkState.ApiReachable, bizTalkState.Problems.Count, bizTalkState.SuspendedTotal);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error checking BizTalk"); }

        FileMonitoringState? fileMonitoringState = null;
        try
        {
            fileMonitoringState = await fileMonitoringTask;
            if (fileMonitoringState != null)
                _logger.LogInformation("    FileMonitoring: {Dirs} dir(s), {Logs} log(s), {Stuck} stuck file(s), {Matches} log match(es)",
                    fileMonitoringState.DirectoriesChecked, fileMonitoringState.LogFilesScanned,
                    fileMonitoringState.StuckFiles.Count, fileMonitoringState.LogMatches.Count);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error checking file monitoring"); }

        Dictionary<string, List<Metric>> metricsByServiceType;
        try { metricsByServiceType = await metricsTask; _logger.LogInformation("    Metrics: collected for {Count} service type(s)", metricsByServiceType.Count); }
        catch (Exception ex) { _logger.LogError(ex, "Error collecting metrics"); metricsByServiceType = new Dictionary<string, List<Metric>>(StringComparer.OrdinalIgnoreCase); }

        // Apply status smoothing to email states: suppress alerts until ConsecutiveFailures reaches
        // minConsecutiveChecks, mirroring the smoothing applied to CPU/memory/disk metrics.
        if (emailProbeStates != null)
        {
            foreach (var state in emailProbeStates.Values)
            {
                if (state.ConsecutiveFailures < minConsecutiveChecks && state.Status != MetricStatus.Healthy)
                {
                    _logger.LogDebug("Email probe '{ProbeId}' status smoothing: ConsecutiveFailures={N} < {Min}, suppressing {Status}",
                        state.ProbeId, state.ConsecutiveFailures, minConsecutiveChecks, state.Status);
                    state.Status = MetricStatus.Healthy;
                }
            }
        }
        if (emailDeliveryStates != null)
        {
            foreach (var state in emailDeliveryStates.Values)
            {
                if (state.ConsecutiveFailures < minConsecutiveChecks && state.Status != MetricStatus.Healthy)
                {
                    _logger.LogDebug("Email delivery check '{CheckId}' status smoothing: ConsecutiveFailures={N} < {Min}, suppressing {Status}",
                        state.CheckId, state.ConsecutiveFailures, minConsecutiveChecks, state.Status);
                    state.Status = MetricStatus.Healthy;
                }
            }
        }

        foreach (var serviceType in currentServer.ServiceTypes)
        {
            _logger.LogInformation("  - Processing service: {ServiceType}", serviceType.Type);

            try
            {
                metricsByServiceType.TryGetValue(serviceType.Type, out var collectedMetrics);
                var status = await ProcessServiceAsync(
                    currentServer,
                    serviceType,
                    maxHistoryEntries,
                    minConsecutiveChecks,
                    appPoolStates,
                    serviceStates,
                    eventLogState,
                    sqlQueryStates,
                    emailProbeStates,
                    emailDeliveryStates,
                    certificateStates,
                    bizTalkState,
                    fileMonitoringState,
                    activeAcknowledges,
                    collectedMetrics ?? new List<Metric>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing service {ServiceType} on server {ServerName}",
                    serviceType.Type, currentServer.Name);
            }
        }

        // Load ALL status files from all servers for the overview page
        _logger.LogInformation("Loading all status files for HTML generation...");
        var allStatusesFromDisk = await _statusStore.LoadAllStatusesAsync();

        _logger.LogInformation("Found {StatusCount} status file(s) from all servers", allStatusesFromDisk.Count);

        // Generate HTML files with ALL statuses (from all servers)
        await _htmlGenerator.GenerateAllAsync(allStatusesFromDisk, config, currentHostname, activeAcknowledges);

        // Copy HTML files to central output path if configured
        await CopyToCentralOutputAsync(config, currentHostname, currentServer);

        // Notifier role (config-driven): if this host is listed in features.notifications.notifierServers,
        // additionally aggregate all servers and send summary mails. Replaces the former --notify flag.
        await MaybeRunNotifierCycleAsync(config, currentHostname);
    }

    /// <summary>
    /// Runs the central notifier cycle when this host is configured as a notifier
    /// (features.notifications.enabled and hostname in notifierServers).
    /// </summary>
    private async Task MaybeRunNotifierCycleAsync(Config config, string currentHostname)
    {
        var notif = config.Features?.Notifications;
        if (notif is not { Enabled: true })
        {
            _logger.LogDebug("Notifier inactive: features.notifications is missing or disabled");
            return;
        }
        if (!notif.NotifierServers.Any(s => s.Equals(currentHostname, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("Notifier inactive: host '{Hostname}' is not in notifierServers [{Servers}]",
                currentHostname, string.Join(", ", notif.NotifierServers));
            return;
        }

        _logger.LogInformation("Running notifier cycle (host '{Hostname}' is a configured notifier, plainTextOnly={PlainTextOnly})",
            currentHostname, notif.PlainTextOnly);
        try
        {
            var sentMails = await _notificationService.RunCycleAsync(config);
            _logger.LogInformation("Notifier cycle complete — {SentMails} mail(s) sent", sentMails);
        }
        catch (Exception ex)
        {
            // The notifier is a best-effort add-on at the end of the run — a failure here
            // must never take down the worker process (metrics/HTML are already written).
            _logger.LogError(ex, "Notifier cycle failed — will retry on the next worker run");
        }
    }

    /// <summary>
    /// Copies the HTML files for the current server to a central output directory if configured.
    /// Only copies files that belong to the current server (e.g., servername.servicetype.html).
    /// </summary>
    private async Task CopyToCentralOutputAsync(Config config, string currentHostname, Server currentServer)
    {
        var centralPath = config.Output?.CentralOutputPath;
        if (string.IsNullOrWhiteSpace(centralPath))
        {
            _logger.LogDebug("No central output path configured, skipping copy");
            return;
        }

        // Replace {hostname} placeholder with actual hostname
        centralPath = centralPath.Replace("{hostname}", currentHostname, StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation("Copying HTML files to central output path: {CentralPath}", centralPath);

        try
        {
            // Create the central directory if it doesn't exist
            Directory.CreateDirectory(centralPath);

            var copiedFiles = 0;
            var wwwrootPath = _htmlGenerator.WwwrootDirectory;

            // Copy server-specific HTML files (e.g., servername.servicetype.html)
            foreach (var serviceType in currentServer.ServiceTypes)
            {
                var htmlFileName = $"{currentHostname}.{serviceType.Type}.html";
                var sourcePath = Path.Combine(wwwrootPath, htmlFileName);
                var destPath = Path.Combine(centralPath, htmlFileName);

                if (File.Exists(sourcePath))
                {
                    await CopyFileAsync(sourcePath, destPath);
                    copiedFiles++;
                    _logger.LogDebug("Copied {FileName} to central output", htmlFileName);
                }
                else
                {
                    _logger.LogWarning("HTML file not found: {SourcePath}", sourcePath);
                }

                // Also copy the status JSON file
                var jsonFileName = $"{currentHostname}.{serviceType.Type}.json";
                var jsonSourcePath = Path.Combine(wwwrootPath, "status", jsonFileName);
                var statusDestDir = Path.Combine(centralPath, "status");
                Directory.CreateDirectory(statusDestDir);
                var jsonDestPath = Path.Combine(statusDestDir, jsonFileName);

                if (File.Exists(jsonSourcePath))
                {
                    await CopyFileAsync(jsonSourcePath, jsonDestPath);
                    copiedFiles++;
                    _logger.LogDebug("Copied {FileName} to central output", jsonFileName);
                }
            }

            _logger.LogInformation("Copied {FileCount} file(s) to central output path", copiedFiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy files to central output path: {CentralPath}", centralPath);
            // Don't throw - this is not critical for the main functionality
        }
    }

    /// <summary>
    /// Copies a file asynchronously
    /// </summary>
    private static async Task CopyFileAsync(string sourcePath, string destPath)
    {
        using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await sourceStream.CopyToAsync(destStream);
    }

    private async Task<T> TrackAsync<T>(string name, Func<Task<T>> taskFactory)
    {
        var logger = _loggerFactory.CreateLogger($"Minicon.SimpleAdmin.Checkers.{name}");
        var sw = Stopwatch.StartNew();
        logger.LogDebug("[{Checker}] starting", name);
        try
        {
            var result = await taskFactory();
            logger.LogDebug("[{Checker}] completed in {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[{Checker}] failed after {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Processes a single service: collects metrics, updates status, saves to disk
    /// </summary>
    private async Task<RuntimeStatus> ProcessServiceAsync(
        Server server,
        ServiceType serviceType,
        int maxHistoryEntries,
        int minConsecutiveChecks,
        Dictionary<string, AppPoolState>? appPoolStates,
        Dictionary<string, ServiceState>? serviceStates,
        EventLogState? eventLogState,
        Dictionary<string, SqlQueryCheckState>? sqlQueryStates,
        Dictionary<string, EmailProbeState>? emailProbeStates,
        Dictionary<string, EmailDeliveryCheckState>? emailDeliveryStates,
        Dictionary<string, CertificateState>? certificateStates,
        BizTalkState? bizTalkState,
        FileMonitoringState? fileMonitoringState,
        IReadOnlyCollection<Acknowledge> activeAcknowledges,
        List<Metric> metrics)
    {
        var timestamp = DateTime.UtcNow;

        // Load existing status to get history for status smoothing
        var existingStatus = await _statusStore.LoadStatusAsync(server.Name, serviceType.Type);
        var history = existingStatus?.History ?? new List<MetricSnapshot>();

        // Evaluate confirmed status for each metric using history
        // This prevents alert fatigue from temporary spikes
        foreach (var metric in metrics)
        {
            var rawStatus = metric.Status;
            var confirmedStatus = _statusEvaluator.GetConfirmedStatus(metric, history, minConsecutiveChecks);

            if (rawStatus != confirmedStatus)
            {
                _logger.LogDebug(
                    "Status smoothing for {MetricName}: raw={RawStatus}, confirmed={ConfirmedStatus}",
                    metric.Name, rawStatus, confirmedStatus);
            }

            metric.Status = confirmedStatus;

            // Calculate average value from the last N measurements (minConsecutiveChecks)
            // History is sorted ascending (oldest first), so we need TakeLast to get the newest
            var recentValues = history
                .Where(h => h.Values.ContainsKey(metric.Name))
                .TakeLast(minConsecutiveChecks - 1)  // Take N-1 from history (leave room for current value)
                .Select(h => h.Values[metric.Name])
                .ToList();

            // Add current value at the end (it's the newest)
            recentValues.Add(metric.Value);

            metric.AverageValue = recentValues.Average();
            metric.AverageSampleCount = recentValues.Count;
        }

        var activeProblems = ProblemDerivationService.DeriveProblems(
            metrics,
            appPoolStates,
            serviceStates,
            eventLogState,
            sqlQueryStates,
            activeAcknowledges,
            server.Name,
            emailProbeStates,
            emailDeliveryStates,
            certificateStates,
            bizTalkState,
            fileMonitoringState);
        var overallStatus = _statusEvaluator.DetermineOverallStatusWithAcknowledges(metrics, activeProblems);

        // Build endpoint status list
        var endpoints = new List<EndpointStatus>();

        if (serviceType.Prtg.Enabled)
        {
            endpoints.Add(new EndpointStatus
            {
                Type = EndpointType.Prtg,
                Path = serviceType.Prtg.AdminPath,
                Enabled = true
            });
        }

        if (serviceType.NetScaler.Enabled)
        {
            endpoints.Add(new EndpointStatus
            {
                Type = EndpointType.NetScaler,
                Path = serviceType.NetScaler.AdminPath,
                Enabled = true
            });
        }

        // Create runtime status
        var status = new RuntimeStatus
        {
            Server = server.Name,
            Service = serviceType.Type,
            LastUpdate = timestamp,
            Status = overallStatus,
            Metrics = metrics,
            Endpoints = endpoints,
            History = new List<MetricSnapshot>(),
            AppPools = appPoolStates,
            Services = serviceStates,
            EventLogs = eventLogState,
            SqlQueryChecks = sqlQueryStates,
            EmailProbes = emailProbeStates,
            EmailDelivery = emailDeliveryStates,
            Certificates = certificateStates,
            BizTalk = bizTalkState,
            FileMonitoring = fileMonitoringState
        };

        // Save status with history
        await _statusStore.SaveStatusAsync(status, maxHistoryEntries);

        _logger.LogInformation("    Status: {Status}, Metrics: {MetricCount}",
            overallStatus, metrics.Count);

        return status;
    }
}
