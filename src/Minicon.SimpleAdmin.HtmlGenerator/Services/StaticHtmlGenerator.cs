using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.HtmlGenerator;

/// <summary>
/// Generates static HTML files from runtime status
/// </summary>
public class StaticHtmlGenerator : IStaticHtmlGenerator
{
    private readonly string _wwwrootDirectory;
    private readonly ILogger<StaticHtmlGenerator> _logger;
    private Config _config = null!;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Gets the wwwroot directory path where HTML files are generated
    /// </summary>
    public string WwwrootDirectory => _wwwrootDirectory;

    /// <summary>
    /// Initializes a new instance of the StaticHtmlGenerator class
    /// </summary>
    /// <param name="wwwrootDirectory">Directory path where HTML files will be generated</param>
    /// <param name="logger">Logger instance</param>
    public StaticHtmlGenerator(string wwwrootDirectory, ILogger<StaticHtmlGenerator> logger)
    {
        _wwwrootDirectory = wwwrootDirectory;
        _logger = logger;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3) // Short timeout for internal network
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        Directory.CreateDirectory(_wwwrootDirectory);
        _logger.LogInformation("StaticHtmlGenerator initialized with directory: {WwwrootDirectory}", _wwwrootDirectory);
    }

    /// <summary>
    /// Generates all HTML files
    /// </summary>
    public async Task GenerateAllAsync(
        List<RuntimeStatus> statuses,
        Config config,
        string currentHostname,
        IReadOnlyCollection<Acknowledge> activeAcknowledges)
    {
        _config = config;
        _logger.LogInformation("Starting HTML generation for {StatusCount} status(es)", statuses.Count);

        // Save status JSON files to wwwroot/status/ for HTTP access by other servers
        _logger.LogDebug("Saving status JSON files for HTTP access");
        await SaveStatusJsonForHttpAsync(statuses);

        // Generate index page
        _logger.LogDebug("Generating index page");
        await GenerateIndexPageAsync(statuses);

        // Generate individual service pages
        _logger.LogDebug("Generating service detail pages");
        foreach (var status in statuses)
        {
            await GenerateServicePageAsync(status, config, activeAcknowledges);
        }

        // Generate endpoint pages for PRTG and NetScaler
        _logger.LogDebug("Generating endpoint pages");
        var endpointCount = 0;
        foreach (var status in statuses)
        {
            foreach (var endpoint in status.Endpoints)
            {
                await GenerateEndpointPageAsync(status, endpoint);
                endpointCount++;
            }
        }

        // Generate documentation page
        _logger.LogDebug("Generating documentation page");
        await GenerateDocumentationPageAsync();

        // Generate all.html page that aggregates status from servers in the same environment
        _logger.LogDebug("Generating all-servers overview page");
        await GenerateAllServersPageAsync(config, currentHostname);

        // Copy status JSON to central path if configured
        if (!string.IsNullOrEmpty(_config.Output?.CentralOutputPath))
        {
            _logger.LogDebug("Copying status JSON to central path");
            await CopyStatusToCentralPathAsync(statuses, currentHostname);
        }

        // Generate central overview if this instance is the central generator
        if (_config.Output?.CentralGenerator?.Enabled == true)
        {
            _logger.LogDebug("Generating central overview from all server statuses");
            await GenerateCentralOverviewFromFilesAsync();
        }

        _logger.LogInformation("HTML generation completed: 1 index page, {ServicePageCount} service page(s), {EndpointPageCount} endpoint page(s), 1 documentation page, 1 all-servers page",
            statuses.Count, endpointCount);
    }

    /// <summary>
    /// Generates the main index page
    /// </summary>
    private async Task GenerateIndexPageAsync(List<RuntimeStatus> statuses)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"de\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"60\">");
        html.AppendLine("    <title>Minicon.SimpleAdmin - System Overview</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetCommonStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <header>");
        html.AppendLine("        <h1>System Status Overview</h1>");
        html.AppendLine($"        <p>Last updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC (auto-refresh: 60s)</p>");
        html.AppendLine("        <p><a href=\"./all.html\" style=\"color: #ecf0f1; text-decoration: underline;\">🌐 Environment Overview</a> | <a href=\"./documentation.html\" style=\"color: #ecf0f1; text-decoration: underline;\">📖 Konfigurationsdokumentation</a></p>");
        html.AppendLine("    </header>");
        html.AppendLine("    <main>");
        html.AppendLine("        <div class=\"grid\">");

        foreach (var status in statuses.OrderBy(s => s.Server).ThenBy(s => s.Service))
        {
            var statusClass = status.Status.ToString().ToLower();
            var statusIcon = GetStatusIcon(status.Status);

            html.AppendLine($"            <div class=\"card card-{statusClass}\">");
            html.AppendLine($"                <div class=\"card-header\">");
            html.AppendLine($"                    <h2>{statusIcon} {status.Server} - {status.Service}</h2>");
            html.AppendLine($"                    <span class=\"status-badge status-{statusClass}\">{status.Status}</span>");
            html.AppendLine($"                </div>");
            html.AppendLine($"                <div class=\"card-body\">");
            html.AppendLine($"                    <p class=\"timestamp\">Last update: {status.LastUpdate:yyyy-MM-dd HH:mm:ss}</p>");

            // Show key metrics
            if (status.Metrics.Count > 0)
            {
                html.AppendLine("                    <div class=\"metrics\">");
                foreach (var metric in status.Metrics.Take(3))
                {
                    var metricClass = metric.Status.ToString().ToLower();
                    html.AppendLine($"                        <div class=\"metric metric-{metricClass}\">");
                    html.AppendLine($"                            <span class=\"metric-name\">{metric.DisplayName}:</span>");
                    html.AppendLine($"                            <span class=\"metric-value\">{metric.Value:F1}{metric.Unit}</span>");
                    html.AppendLine($"                        </div>");
                }
                html.AppendLine("                    </div>");
            }

            // Monitoring badges
            html.AppendLine(GenerateMonitoringBadgesHtml(status, indentLevel: 5));

            // Links
            html.AppendLine($"                    <div class=\"links\">");
            html.AppendLine($"                        <a href=\"./{status.Server}.{status.Service}.html\" class=\"link link-details\">Details</a>");
            foreach (var endpoint in status.Endpoints.Where(e => e.Enabled))
            {
                // Use relative path to preserve port in browser navigation
                var relativePath = endpoint.Path.TrimStart('/');
                html.AppendLine($"                        <a href=\"./{relativePath}/\" class=\"link\">{endpoint.Type}</a>");
            }
            html.AppendLine($"                    </div>");
            html.AppendLine($"                </div>");
            html.AppendLine($"            </div>");
        }

        html.AppendLine("        </div>");
        html.AppendLine("    </main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var filePath = Path.Combine(_wwwrootDirectory, "index.html");
        await File.WriteAllTextAsync(filePath, html.ToString());
        _logger.LogInformation("Generated index page: {FilePath}", filePath);
    }

    /// <summary>
    /// Generates a service detail page
    /// </summary>
    private async Task GenerateServicePageAsync(
        RuntimeStatus status,
        Config config,
        IReadOnlyCollection<Acknowledge> activeAcknowledges)
    {
        var minConsecutiveChecks = _config.History?.MinConsecutiveChecks ?? 5;

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"de\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"60\">");
        html.AppendLine($"    <title>{status.Server} - {status.Service}</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetCommonStyles());
        html.AppendLine(GetDetailPageStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <header>");
        html.AppendLine($"        <h1>{status.Server} - {status.Service}</h1>");
        html.AppendLine($"        <p>Status: <span class=\"status-badge status-{status.Status.ToString().ToLower()}\">{status.Status}</span></p>");
        html.AppendLine($"        <p>Last updated: {status.LastUpdate:yyyy-MM-dd HH:mm:ss} UTC (auto-refresh: 60s)</p>");
        html.AppendLine("        <p><a href=\"./documentation.html\" style=\"color: #ecf0f1; text-decoration: underline;\">📖 Konfigurationsdokumentation</a> | <a href=\"./\" style=\"color: #ecf0f1; text-decoration: underline;\">← Zurück zur Übersicht</a></p>");
        html.AppendLine("    </header>");
        html.AppendLine("    <main>");

        // Metrics section
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>Current Metrics</h2>");
        html.AppendLine("            <div class=\"metrics-grid\">");
        foreach (var metric in status.Metrics)
        {
            var metricClass = metric.Status.ToString().ToLower();
            html.AppendLine($"                <div class=\"metric-card metric-{metricClass}\">");
            html.AppendLine($"                    <h3>{metric.DisplayName}</h3>");
            html.AppendLine($"                    <p class=\"metric-value-large\">{metric.Value:F2} {metric.Unit}</p>");
            html.AppendLine($"                    <p class=\"metric-status\">Status: {metric.Status}</p>");

            // Show average value if available
            if (metric.AverageValue.HasValue)
            {
                var avgClass = GetAverageStatusClass(metric.AverageValue.Value, metric.Threshold);
                html.AppendLine($"                    <p class=\"metric-average {avgClass}\">⌀ Durchschnitt (letzte {metric.AverageSampleCount}): <strong>{metric.AverageValue.Value:F2} {metric.Unit}</strong></p>");
            }

            if (metric.Threshold != null)
            {
                var thresholdText = FormatThresholdWithOperator(metric.Threshold);
                html.AppendLine($"                    <p class=\"threshold\">{thresholdText}</p>");
            }

            // Add history bar chart if enough data
            var historyResult = GenerateHistoryBars(metric.Name, status.History, metric.Threshold);
            if (historyResult.HasValue)
            {
                var (historyHtml, totalEntries, barCount, exactBars) = historyResult.Value;
                var historyLabel = totalEntries <= 20
                    ? $"History: {totalEntries} Einträge (alle 1:1)"
                    : $"History: {totalEntries} Einträge → {barCount} Balken (letzte {exactBars} exakt)";
                html.AppendLine($"                    <div class=\"history-container\">");
                html.AppendLine($"                        <p class=\"history-label\">{historyLabel}</p>");
                html.AppendLine($"                        {historyHtml}");
                html.AppendLine($"                    </div>");
            }

            html.AppendLine($"                </div>");
        }
        html.AppendLine("            </div>");
        html.AppendLine("        </section>");

        // Monitoring badges section
        var badgesHtml = GenerateMonitoringBadgesHtml(status, indentLevel: 3);
        if (!string.IsNullOrEmpty(badgesHtml))
        {
            html.AppendLine("        <section>");
            html.AppendLine("            <h2>Monitoring Status</h2>");
            html.AppendLine(badgesHtml);
            html.AppendLine("        </section>");
        }

        // AppPool section from RuntimeStatus
        if (status.AppPools != null && status.AppPools.Count > 0)
        {
            var appPoolHtml = GenerateAppPoolSectionHtml(status.AppPools);
            if (!string.IsNullOrEmpty(appPoolHtml))
            {
                html.AppendLine(appPoolHtml);
            }
        }

        // Windows Services section from RuntimeStatus
        if (status.Services != null && status.Services.Count > 0)
        {
            var servicesHtml = GenerateWindowsServicesSectionHtml(status.Services);
            if (!string.IsNullOrEmpty(servicesHtml))
            {
                html.AppendLine(servicesHtml);
            }
        }


        // Event Log section from RuntimeStatus
        if (status.EventLogs != null && status.EventLogs.RecentEvents.Count > 0)
        {
            var eventLogHtml = GenerateEventLogSectionHtml(status.EventLogs);
            if (!string.IsNullOrEmpty(eventLogHtml))
            {
                html.AppendLine(eventLogHtml);
            }
        }

        // SQL Query Checks section from RuntimeStatus
        if (status.SqlQueryChecks != null && status.SqlQueryChecks.Count > 0)
        {
            var sqlHtml = GenerateSqlQueryChecksSectionHtml(status.SqlQueryChecks);
            if (!string.IsNullOrEmpty(sqlHtml))
            {
                html.AppendLine(sqlHtml);
            }
        }

        // Email Probe section (sender role)
        if (status.EmailProbes != null && status.EmailProbes.Count > 0)
        {
            var emailProbeHtml = GenerateEmailProbesSectionHtml(status.EmailProbes);
            if (!string.IsNullOrEmpty(emailProbeHtml))
                html.AppendLine(emailProbeHtml);
        }

        // Email Delivery section (checker role)
        if (status.EmailDelivery != null && status.EmailDelivery.Count > 0)
        {
            var emailDeliveryHtml = GenerateEmailDeliverySectionHtml(status.EmailDelivery);
            if (!string.IsNullOrEmpty(emailDeliveryHtml))
                html.AppendLine(emailDeliveryHtml);
        }

        // Certificate section
        if (status.Certificates != null && status.Certificates.Count > 0)
        {
            var certHtml = GenerateCertificateSectionHtml(status.Certificates);
            if (!string.IsNullOrEmpty(certHtml))
                html.AppendLine(certHtml);
        }

        // BizTalk section
        if (status.BizTalk != null)
        {
            var bizTalkHtml = GenerateBizTalkSectionHtml(status.BizTalk);
            if (!string.IsNullOrEmpty(bizTalkHtml))
                html.AppendLine(bizTalkHtml);
        }

        // File monitoring section
        if (status.FileMonitoring != null)
        {
            var fileMonHtml = GenerateFileMonitoringSectionHtml(status.FileMonitoring);
            if (!string.IsNullOrEmpty(fileMonHtml))
                html.AppendLine(fileMonHtml);
        }

        // Problems section derived from metrics + AppPools + Services + EventLogs + SqlQueryChecks + EmailProbes + EmailDelivery + Certificates
        var problems = ProblemDerivationService.DeriveProblems(
            status.Metrics,
            status.AppPools,
            status.Services,
            status.EventLogs,
            status.SqlQueryChecks,
            activeAcknowledges,
            status.Server,
            status.EmailProbes,
            status.EmailDelivery,
            status.Certificates,
            status.BizTalk,
            status.FileMonitoring);
        if (problems.Count > 0)
        {
            var problemsHtml = GenerateProblemsSectionHtml(problems);
            if (!string.IsNullOrEmpty(problemsHtml))
            {
                html.AppendLine(problemsHtml);
            }
        }

        // Legend section
        var historyCount = status.History.Count;
        var maxBars = 20;
        var showsAllOneToOne = historyCount <= maxBars;

        html.AppendLine("        <section class=\"legend-section\">");
        html.AppendLine("            <h2>Legende</h2>");
        html.AppendLine("            <div class=\"legend-grid\">");
        html.AppendLine("                <div class=\"legend-item\">");
        html.AppendLine("                    <h4>Balkenfarben</h4>");
        html.AppendLine("                    <div class=\"legend-color\"><span class=\"color-box\" style=\"background: #27ae60;\"></span> OK - Wert innerhalb der Schwellwerte</div>");
        html.AppendLine("                    <div class=\"legend-color\"><span class=\"color-box\" style=\"background: #f39c12;\"></span> Warning - Warnschwelle überschritten</div>");
        html.AppendLine("                    <div class=\"legend-color\"><span class=\"color-box\" style=\"background: #e74c3c;\"></span> Critical - Kritischer Schwellwert überschritten</div>");
        html.AppendLine("                </div>");
        html.AppendLine("                <div class=\"legend-item\">");
        html.AppendLine("                    <h4>History-Darstellung</h4>");
        if (showsAllOneToOne)
        {
            html.AppendLine($"                    <p>Aktuell {historyCount} Einträge - alle werden 1:1 angezeigt.</p>");
            html.AppendLine($"                    <p>Ab {maxBars + 1} Einträgen werden ältere Daten logarithmisch aggregiert.</p>");
        }
        else
        {
            html.AppendLine($"                    <p>Die History zeigt max. {maxBars} Balken mit logarithmischer Verteilung:</p>");
            html.AppendLine("                    <ul>");
            html.AppendLine($"                        <li><strong>Rechte {minConsecutiveChecks} Balken:</strong> Letzte {minConsecutiveChecks} Messungen (1:1, für Statusprüfung)</li>");
            html.AppendLine($"                        <li><strong>Linke {maxBars - minConsecutiveChecks} Balken:</strong> Ältere Daten (aggregiert, Durchschnitt)</li>");
            html.AppendLine("                        <li>Je älter die Daten, desto mehr Einträge pro Balken</li>");
            html.AppendLine("                    </ul>");
        }
        html.AppendLine("                </div>");
        html.AppendLine("                <div class=\"legend-item\">");
        html.AppendLine("                    <h4>Status-Glättung</h4>");
        html.AppendLine($"                    <p>Ein Statuswechsel wird erst bestätigt, wenn <strong>{minConsecutiveChecks} aufeinanderfolgende</strong> Messungen denselben Status zeigen.</p>");
        html.AppendLine("                    <p>Dies verhindert Fehlalarme durch kurze CPU/Memory-Spitzen.</p>");
        html.AppendLine("                </div>");
        html.AppendLine("            </div>");
        html.AppendLine("        </section>");

        html.AppendLine("    </main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var fileName = $"{status.Server}.{status.Service}.html";
        var filePath = Path.Combine(_wwwrootDirectory, fileName);
        await File.WriteAllTextAsync(filePath, html.ToString());
        _logger.LogDebug("Generated service page for {Server}.{Service}: {FilePath}",
            status.Server, status.Service, filePath);
    }

    /// <summary>
    /// Generates endpoint pages for PRTG/NetScaler
    /// </summary>
    private async Task GenerateEndpointPageAsync(RuntimeStatus status, EndpointStatus endpoint)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html>");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine($"    <title>{endpoint.Type} - {status.Server} - {status.Service}</title>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"    <h1>{endpoint.Type} Status: {status.Status}</h1>");
        html.AppendLine($"    <p>Server: {status.Server}</p>");
        html.AppendLine($"    <p>Service: {status.Service}</p>");
        html.AppendLine($"    <p>Last Update: {status.LastUpdate:yyyy-MM-dd HH:mm:ss} UTC</p>");
        html.AppendLine("    <p><a href=\"../../../documentation.html\">📖 Konfigurationsdokumentation</a> | <a href=\"../../../\">← Zurück zur Übersicht</a></p>");

        // Show metrics
        if (status.Metrics.Count > 0)
        {
            html.AppendLine("    <h2>Metrics:</h2>");
            html.AppendLine("    <ul>");
            foreach (var metric in status.Metrics)
            {
                html.AppendLine($"        <li>{metric.DisplayName}: {metric.Value:F2} {metric.Unit} ({metric.Status})</li>");
            }
            html.AppendLine("    </ul>");
        }

        // Add OK marker as HTML comment
        // NetScaler: OK-Marker bei Healthy, Degraded und Acknowledged (nur bei Critical aus LoadBalancer nehmen)
        // PRTG: OK-Marker nur bei Healthy oder Acknowledged (Problem wird bearbeitet)
        var okMarker = _config.Output?.OkMarker ?? "##OK##";
        var showOkMarker = endpoint.Type == EndpointType.NetScaler
            ? status.Status == ServiceStatus.Healthy || status.Status == ServiceStatus.Degraded || status.Status == ServiceStatus.Acknowledged
            : status.Status == ServiceStatus.Healthy || status.Status == ServiceStatus.Acknowledged;

        if (showOkMarker)
        {
            html.AppendLine($"    <!-- Status: {okMarker} -->");
        }

        html.AppendLine($"    <p>Status: {(status.Status == ServiceStatus.Healthy ? "OK" : status.Status.ToString())} (HTTP 200)</p>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        // Create directory structure for endpoint
        // Path like "/admin/api-gateway/monitor" becomes "admin/api-gateway/monitor/index.html"
        var path = endpoint.Path.TrimStart('/');
        var directory = Path.Combine(_wwwrootDirectory, path);
        Directory.CreateDirectory(directory);
        _logger.LogDebug("Created directory for endpoint: {Directory}", directory);

        // Write index.html so the loadbalancer can access the directory directly
        var filePath = Path.Combine(directory, "index.html");
        await File.WriteAllTextAsync(filePath, html.ToString());
        _logger.LogDebug("Generated endpoint page for {EndpointType} ({Server}.{Service}): {FilePath}",
            endpoint.Type, status.Server, status.Service, filePath);
    }

    /// <summary>
    /// Returns common CSS styles
    /// </summary>
    private string GetCommonStyles()
    {
        return @"
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f5f5f5; color: #333; }
            header { background: #2c3e50; color: white; padding: 2rem; text-align: center; }
            header h1 { font-size: 2rem; margin-bottom: 0.5rem; }
            main { max-width: 1200px; margin: 2rem auto; padding: 0 1rem; }
            .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(350px, 1fr)); gap: 1.5rem; }
            .card { background: white; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); overflow: hidden; }
            .card-header { padding: 1rem; border-bottom: 2px solid #ecf0f1; display: flex; justify-content: space-between; align-items: center; }
            .card-header h2 { font-size: 1.2rem; display: flex; align-items: center; gap: 0.5rem; }
            .card-body { padding: 1rem; }
            .status-badge { padding: 0.25rem 0.75rem; border-radius: 4px; font-size: 0.85rem; font-weight: bold; }
            .status-healthy { background: #27ae60; color: white; }
            .status-degraded { background: #f39c12; color: white; }
            .status-unhealthy { background: #e74c3c; color: white; }
            .status-unknown { background: #95a5a6; color: white; }
            .status-unreachable { background: #34495e; color: white; }
            .status-acknowledged { background: #9b59b6; color: white; }
            .card-healthy { border-left: 4px solid #27ae60; }
            .card-degraded { border-left: 4px solid #f39c12; }
            .card-unhealthy { border-left: 4px solid #e74c3c; }
            .card-unknown { border-left: 4px solid #95a5a6; }
            .card-unreachable { border-left: 4px solid #34495e; background: #f8f8f8; }
            .card-acknowledged { border-left: 4px solid #9b59b6; background: #f5f0fa; }
            .acknowledge-banner { background: #9b59b6; color: white; padding: 0.75rem 1rem; margin-bottom: 1rem; border-radius: 6px; display: flex; align-items: center; gap: 0.75rem; }
            .acknowledge-banner .ack-icon { font-size: 1.25rem; }
            .acknowledge-banner .ack-text { flex: 1; }
            .acknowledge-banner .ack-info { font-size: 0.85rem; opacity: 0.9; }
            .apppool-section { margin-top: 1.5rem; }
            .apppool-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 1rem; }
            .apppool-card { background: #f8f9fa; border-radius: 8px; padding: 1rem; border-left: 4px solid #3498db; }
            .apppool-card.pool-running { border-left-color: #27ae60; }
            .apppool-card.pool-stopped { border-left-color: #e74c3c; }
            .apppool-card.pool-warning { border-left-color: #f39c12; }
            .apppool-name { font-weight: 600; font-size: 1rem; margin-bottom: 0.5rem; }
            .apppool-status { font-size: 0.85rem; display: flex; gap: 0.5rem; flex-wrap: wrap; }
            .apppool-badge { padding: 0.2rem 0.5rem; border-radius: 4px; font-size: 0.75rem; }
            .apppool-badge.running { background: #d5f4e6; color: #1e7e34; }
            .apppool-badge.stopped { background: #fadbd8; color: #721c24; }
            .apppool-badge.memory-ok { background: #e8f4fd; color: #0c5460; }
            .apppool-badge.memory-warning { background: #fef5e7; color: #856404; }
            .apppool-badge.memory-critical { background: #fadbd8; color: #721c24; }
            .sql-result-table { width: 100%; border-collapse: collapse; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.8rem; margin-top: 0.5rem; }
            .sql-result-table th, .sql-result-table td { border: 1px solid #dcdcdc; padding: 4px 8px; text-align: left; vertical-align: top; }
            .sql-result-table th { background: #f0f0f0; font-weight: 600; }
            .sql-result-table tbody tr:nth-child(even) { background: #fafafa; }
            .sql-result-note { margin-top: 0.5rem; color: #7f8c8d; font-size: 0.8rem; font-style: italic; }
            .status-message { font-size: 0.85rem; color: #7f8c8d; font-style: italic; margin-top: 0.5rem; }
            .timestamp { color: #7f8c8d; font-size: 0.9rem; margin-bottom: 1rem; }
            .metrics { display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 1rem; }
            .metric { display: flex; justify-content: space-between; padding: 0.5rem; background: #ecf0f1; border-radius: 4px; }
            .metric-ok { background: #d5f4e6; }
            .metric-warning { background: #fef5e7; }
            .metric-critical { background: #fadbd8; }
            .metric-name { font-weight: 500; }
            .metric-value { font-weight: bold; }
            .links { display: flex; gap: 0.5rem; }
            .link { padding: 0.5rem 1rem; background: #3498db; color: white; text-decoration: none; border-radius: 4px; font-size: 0.9rem; }
            .link:hover { background: #2980b9; }
            .link-details { background: #9b59b6; }
            .link-details:hover { background: #8e44ad; }
            .metrics-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 1rem; }
            .metric-card { background: #ecf0f1; padding: 1rem; border-radius: 8px; }
            .metric-value-large { font-size: 2rem; font-weight: bold; margin: 0.5rem 0; }
            .metric-status { font-size: 0.9rem; color: #7f8c8d; }
            .threshold { font-size: 0.85rem; color: #95a5a6; margin-top: 0.5rem; }
            .history-container { margin-top: 1rem; padding-top: 0.75rem; border-top: 1px solid #ddd; }
            .history-label { font-size: 0.75rem; color: #7f8c8d; margin-bottom: 0.5rem; }
            .history-bars { display: flex; align-items: flex-end; gap: 2px; height: 40px; background: #f8f9fa; border-radius: 4px; padding: 4px; }
            .history-bar { flex: 1; min-width: 4px; border-radius: 2px 2px 0 0; }
            .monitoring-badge { padding: 0.4rem 0.6rem; border-radius: 4px; font-size: 0.8rem; font-weight: 500; white-space: nowrap; display: inline-block; }
            .badge-ok { background: #27ae60; color: white; }
            .badge-warning { background: #f39c12; color: white; }
            .badge-critical { background: #e74c3c; color: white; }
        ";
    }

    /// <summary>
    /// Returns additional CSS styles for detail pages
    /// </summary>
    private string GetDetailPageStyles()
    {
        return @"
            .legend-section { margin-top: 2rem; }
            .legend-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 1.5rem; }
            .legend-item { background: #f8f9fa; padding: 1rem; border-radius: 8px; border-left: 4px solid #3498db; }
            .legend-item h4 { margin-bottom: 0.75rem; color: #2c3e50; }
            .legend-item p { margin-bottom: 0.5rem; font-size: 0.9rem; }
            .legend-item ul { margin-left: 1.5rem; font-size: 0.9rem; }
            .legend-item li { margin-bottom: 0.25rem; }
            .legend-color { display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.5rem; font-size: 0.9rem; }
            .color-box { width: 20px; height: 20px; border-radius: 4px; display: inline-block; }
            .threshold-info { font-family: monospace; background: #ecf0f1; padding: 0.2rem 0.4rem; border-radius: 3px; }
            .metric-average { font-size: 0.9rem; margin-top: 0.5rem; padding: 0.5rem; border-radius: 4px; background: #f8f9fa; }
            .metric-average.avg-ok { background: #d5f4e6; color: #1e7e34; }
            .metric-average.avg-warning { background: #fef5e7; color: #856404; }
            .metric-average.avg-critical { background: #fadbd8; color: #721c24; }
            .problems-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 1rem; }
            .problem-card { background: #fff; padding: 1rem; border-radius: 8px; border-left: 4px solid #e74c3c; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
            .problem-card.problem-warning { border-left-color: #f39c12; }
            .problem-card.problem-critical { border-left-color: #e74c3c; }
            .problem-card h3 { margin-bottom: 0.5rem; color: #2c3e50; }
            .problem-card p { margin-bottom: 0.25rem; font-size: 0.9rem; }
            .severity-badge { padding: 0.2rem 0.5rem; border-radius: 3px; font-size: 0.8rem; font-weight: bold; }
            .severity-badge.severity-critical { background: #fadbd8; color: #721c24; }
            .severity-badge.severity-warning { background: #fef5e7; color: #856404; }
        ";
    }

    /// <summary>
    /// Gets the CSS class for the average value display based on threshold evaluation
    /// </summary>
    /// <param name="averageValue">The average value to evaluate</param>
    /// <param name="threshold">The threshold to evaluate against</param>
    /// <returns>CSS class name (avg-ok, avg-warning, avg-critical)</returns>
    private string GetAverageStatusClass(double averageValue, MetricThreshold? threshold)
    {
        if (threshold == null)
        {
            return "avg-ok";
        }

        var op = threshold.Operator ?? ">";

        bool isCritical = op switch
        {
            ">" => threshold.Critical.HasValue && averageValue > threshold.Critical.Value,
            "<" => threshold.Critical.HasValue && averageValue < threshold.Critical.Value,
            ">=" => threshold.Critical.HasValue && averageValue >= threshold.Critical.Value,
            "<=" => threshold.Critical.HasValue && averageValue <= threshold.Critical.Value,
            "==" => threshold.Critical.HasValue && Math.Abs(averageValue - threshold.Critical.Value) < 0.01,
            "!=" => threshold.Critical.HasValue && Math.Abs(averageValue - threshold.Critical.Value) >= 0.01,
            _ => false
        };

        if (isCritical) return "avg-critical";

        bool isWarning = op switch
        {
            ">" => threshold.Warning.HasValue && averageValue > threshold.Warning.Value,
            "<" => threshold.Warning.HasValue && averageValue < threshold.Warning.Value,
            ">=" => threshold.Warning.HasValue && averageValue >= threshold.Warning.Value,
            "<=" => threshold.Warning.HasValue && averageValue <= threshold.Warning.Value,
            "==" => threshold.Warning.HasValue && Math.Abs(averageValue - threshold.Warning.Value) < 0.01,
            "!=" => threshold.Warning.HasValue && Math.Abs(averageValue - threshold.Warning.Value) >= 0.01,
            _ => false
        };

        return isWarning ? "avg-warning" : "avg-ok";
    }

    /// <summary>
    /// Formats threshold display with operator for better clarity
    /// </summary>
    /// <param name="threshold">The threshold to format</param>
    /// <returns>Formatted threshold string like "Warning: >70%, Critical: >85%"</returns>
    private string FormatThresholdWithOperator(MetricThreshold threshold)
    {
        var op = threshold.Operator ?? ">";
        var opDisplay = op switch
        {
            ">" => ">",
            ">=" => "≥",
            "<" => "<",
            "<=" => "≤",
            "==" => "=",
            "!=" => "≠",
            _ => op
        };

        var parts = new List<string>();

        if (threshold.Warning.HasValue)
        {
            parts.Add($"Warning: {opDisplay}{threshold.Warning.Value}%");
        }

        if (threshold.Critical.HasValue)
        {
            parts.Add($"Critical: {opDisplay}{threshold.Critical.Value}%");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets status icon
    /// </summary>
    private string GetStatusIcon(ServiceStatus status)
    {
        return status switch
        {
            ServiceStatus.Healthy => "✅",
            ServiceStatus.Degraded => "⚠️",
            ServiceStatus.Unhealthy => "❌",
            ServiceStatus.Unreachable => "🔌",
            _ => "❓"
        };
    }

    /// <summary>
    /// Generates a bar chart showing historical values for a metric with logarithmic distribution.
    /// The last minConsecutiveChecks entries are shown 1:1, older entries are logarithmically aggregated.
    /// </summary>
    /// <param name="metricName">Name of the metric to extract from history</param>
    /// <param name="history">List of historical snapshots</param>
    /// <param name="threshold">Optional threshold for coloring bars</param>
    /// <param name="maxBars">Maximum number of bars to display (default: 20)</param>
    /// <returns>Tuple with HTML string, total entries, bar count, and exact bars count (or null if insufficient data)</returns>
    private (string Html, int TotalEntries, int BarCount, int ExactBars)? GenerateHistoryBars(string metricName, List<MetricSnapshot> history, MetricThreshold? threshold, int maxBars = 20)
    {
        // Extract values for this metric from history (ordered by timestamp descending = newest first)
        var values = history
            .OrderByDescending(h => h.Timestamp)
            .Where(h => h.Values.ContainsKey(metricName))
            .Select(h => h.Values[metricName])
            .ToList();

        if (values.Count < 6)
        {
            return null; // Not enough data points
        }

        var minConsecutiveChecks = _config.History?.MinConsecutiveChecks ?? 5;
        var aggregatedValues = GetLogarithmicAggregatedValues(values, maxBars, minConsecutiveChecks);

        if (aggregatedValues.Count < 6)
        {
            return null; // Not enough data points after aggregation
        }

        // Calculate how many bars are shown 1:1 (exact) vs aggregated
        var actualExactBars = Math.Min(minConsecutiveChecks, values.Count);
        if (values.Count <= maxBars)
        {
            // All bars are 1:1 when we have fewer entries than maxBars
            actualExactBars = values.Count;
        }

        var html = new StringBuilder();
        html.AppendLine($"<div class=\"history-bars\" title=\"{values.Count} Einträge → {aggregatedValues.Count} Balken\">");

        foreach (var value in aggregatedValues)
        {
            var percentage = Math.Clamp(value, 0, 100);
            var barColor = GetBarColor(value, threshold);
            html.AppendLine($"  <div class=\"history-bar\" style=\"height: {percentage:F0}%; background: {barColor};\" title=\"{value:F1}%\"></div>");
        }

        html.AppendLine("</div>");
        return (html.ToString(), values.Count, aggregatedValues.Count, actualExactBars);
    }

    /// <summary>
    /// Aggregates history values logarithmically: newest entries 1:1, older entries aggregated with increasing bucket sizes.
    /// </summary>
    /// <param name="values">Values ordered by timestamp descending (newest first)</param>
    /// <param name="maxBars">Maximum number of bars to display</param>
    /// <param name="exactBars">Number of newest entries to show 1:1 (minConsecutiveChecks)</param>
    /// <returns>List of aggregated values ordered from oldest to newest</returns>
    private List<double> GetLogarithmicAggregatedValues(List<double> values, int maxBars, int exactBars)
    {
        var result = new List<double>();

        // Ensure exactBars doesn't exceed maxBars
        exactBars = Math.Min(exactBars, maxBars);
        var aggregatedBars = maxBars - exactBars;

        // If we have fewer or equal values than maxBars, show all 1:1
        if (values.Count <= maxBars)
        {
            // Reverse to get oldest first
            result.AddRange(values.AsEnumerable().Reverse());
            return result;
        }

        // Split: newest exactBars entries (1:1) and the rest (to be aggregated)
        var exactValues = values.Take(exactBars).ToList(); // newest entries
        var remainingValues = values.Skip(exactBars).ToList(); // older entries

        if (remainingValues.Count == 0 || aggregatedBars <= 0)
        {
            // Only exact values, reverse to get oldest first
            result.AddRange(exactValues.AsEnumerable().Reverse());
            return result;
        }

        // Calculate logarithmic bucket sizes for remaining values
        var bucketSizes = CalculateLogarithmicBucketSizes(remainingValues.Count, aggregatedBars);

        // Aggregate older values into buckets (remainingValues is newest-to-oldest, buckets go from newest-old to oldest)
        var currentIndex = 0;
        var aggregatedBuckets = new List<double>();

        foreach (var bucketSize in bucketSizes)
        {
            if (currentIndex >= remainingValues.Count)
                break;

            var bucketValues = remainingValues.Skip(currentIndex).Take(bucketSize).ToList();
            if (bucketValues.Count > 0)
            {
                aggregatedBuckets.Add(bucketValues.Average());
            }
            currentIndex += bucketSize;
        }

        // Build result: oldest (aggregated) first, then newest (1:1)
        // aggregatedBuckets is in order from "newest old" to "oldest", so reverse it
        aggregatedBuckets.Reverse();
        result.AddRange(aggregatedBuckets);

        // Add exact values (reverse to get oldest of the exact ones first)
        exactValues.Reverse();
        result.AddRange(exactValues);

        return result;
    }

    /// <summary>
    /// Calculates logarithmically increasing bucket sizes.
    /// Smaller buckets for newer data, larger buckets for older data.
    /// </summary>
    /// <param name="totalEntries">Total number of entries to distribute</param>
    /// <param name="bucketCount">Number of buckets to create</param>
    /// <returns>List of bucket sizes (first = smallest/newest, last = largest/oldest)</returns>
    private List<int> CalculateLogarithmicBucketSizes(int totalEntries, int bucketCount)
    {
        if (bucketCount <= 0 || totalEntries <= 0)
            return new List<int>();

        // Use exponential growth for bucket sizes
        // Bucket sizes: b_i = base^i, normalized to sum to totalEntries
        var logBase = Math.Pow(totalEntries, 1.0 / bucketCount);

        var rawSizes = new List<double>();
        for (int i = 0; i < bucketCount; i++)
        {
            // Smaller exponent = smaller bucket (for newer data)
            // i=0 is newest-old data, i=bucketCount-1 is oldest data
            rawSizes.Add(Math.Pow(logBase, i));
        }

        var rawSum = rawSizes.Sum();
        var bucketSizes = rawSizes.Select(s => (int)Math.Round(s / rawSum * totalEntries)).ToList();

        // Adjust for rounding errors - add/remove from largest bucket
        var diff = totalEntries - bucketSizes.Sum();
        if (diff != 0 && bucketSizes.Count > 0)
        {
            var largestIndex = bucketSizes.IndexOf(bucketSizes.Max());
            bucketSizes[largestIndex] += diff;
        }

        // Ensure no bucket is empty (minimum size 1)
        for (int i = 0; i < bucketSizes.Count; i++)
        {
            if (bucketSizes[i] <= 0)
            {
                bucketSizes[i] = 1;
            }
        }

        return bucketSizes;
    }

    /// <summary>
    /// Gets the color for a bar based on value and threshold
    /// </summary>
    private string GetBarColor(double value, MetricThreshold? threshold)
    {
        if (threshold == null)
        {
            return "#3498db"; // Default blue
        }

        var op = threshold.Operator ?? ">";

        bool isCritical = op switch
        {
            ">" => threshold.Critical.HasValue && value > threshold.Critical.Value,
            "<" => threshold.Critical.HasValue && value < threshold.Critical.Value,
            ">=" => threshold.Critical.HasValue && value >= threshold.Critical.Value,
            "<=" => threshold.Critical.HasValue && value <= threshold.Critical.Value,
            _ => false
        };

        if (isCritical) return "#e74c3c";

        bool isWarning = op switch
        {
            ">" => threshold.Warning.HasValue && value > threshold.Warning.Value,
            "<" => threshold.Warning.HasValue && value < threshold.Warning.Value,
            ">=" => threshold.Warning.HasValue && value >= threshold.Warning.Value,
            "<=" => threshold.Warning.HasValue && value <= threshold.Warning.Value,
            _ => false
        };

        return isWarning ? "#f39c12" : "#27ae60";
    }

    /// <summary>
    /// Generates the documentation page
    /// </summary>
    private async Task GenerateDocumentationPageAsync()
    {
        var html = GetDocumentationHtml();
        var filePath = Path.Combine(_wwwrootDirectory, "documentation.html");
        await File.WriteAllTextAsync(filePath, html);
        _logger.LogInformation("Generated documentation page: {FilePath}", filePath);
    }

    /// <summary>
    /// Returns the complete documentation HTML
    /// </summary>
    private string GetDocumentationHtml()
    {
        // Load documentation from Resources directory
        var resourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "documentation-template.html");

        if (File.Exists(resourcePath))
        {
            _logger.LogDebug("Loading documentation from: {ResourcePath}", resourcePath);
            return File.ReadAllText(resourcePath);
        }

        _logger.LogWarning("Documentation template not found at {ResourcePath}, using fallback", resourcePath);

        // Fallback documentation - return minimal page
        return GetFallbackDocumentationHtml();
    }

    /// <summary>
    /// Saves status JSON files to wwwroot/status/ for HTTP access by other servers
    /// </summary>
    private async Task SaveStatusJsonForHttpAsync(List<RuntimeStatus> statuses)
    {
        var statusDir = Path.Combine(_wwwrootDirectory, "status");
        Directory.CreateDirectory(statusDir);

        foreach (var status in statuses)
        {
            var fileName = $"{status.Server}.{status.Service}.json";
            var filePath = Path.Combine(statusDir, fileName);

            // Create a simplified status without full history for HTTP transfer
            var httpStatus = new RuntimeStatus
            {
                Server = status.Server,
                Service = status.Service,
                LastUpdate = status.LastUpdate,
                Status = status.Status,
                StatusMessage = status.StatusMessage,
                Metrics = status.Metrics,
                Endpoints = status.Endpoints,
                History = new List<MetricSnapshot>(), // Don't include full history in HTTP status
                AppPools = status.AppPools,
                Services = status.Services,
                EventLogs = status.EventLogs,
                SqlQueryChecks = status.SqlQueryChecks,
                EmailProbes = status.EmailProbes,
                EmailDelivery = status.EmailDelivery,
                Certificates = status.Certificates,
                BizTalk = status.BizTalk,
                FileMonitoring = status.FileMonitoring
            };

            var json = JsonSerializer.Serialize(httpStatus, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogDebug("Saved status JSON for HTTP: {FilePath}", filePath);
        }

        // Additionally write an aggregated {hostname}.status.json (CentralServerStatus) so the
        // central notifier can pull a single file per server over HTTP. Atomic write (temp + move).
        var hostname = statuses.FirstOrDefault()?.Server;
        if (!string.IsNullOrEmpty(hostname))
        {
            var combined = new CentralServerStatus
            {
                Hostname = hostname,
                GeneratedAt = DateTime.UtcNow,
                Statuses = statuses.Select(s => new RuntimeStatus
                {
                    Server = s.Server,
                    Service = s.Service,
                    LastUpdate = s.LastUpdate,
                    Status = s.Status,
                    StatusMessage = s.StatusMessage,
                    Metrics = s.Metrics,
                    Endpoints = s.Endpoints,
                    History = new List<MetricSnapshot>(), // Don't include history in HTTP status
                    AppPools = s.AppPools,
                    Services = s.Services,
                    EventLogs = s.EventLogs,
                    SqlQueryChecks = s.SqlQueryChecks,
                    EmailProbes = s.EmailProbes,
                    EmailDelivery = s.EmailDelivery,
                    Certificates = s.Certificates,
                    BizTalk = s.BizTalk,
                    FileMonitoring = s.FileMonitoring
                }).ToList()
            };

            var aggFileName = $"{hostname}.status.json";
            var aggTempPath = Path.Combine(statusDir, $"{aggFileName}.tmp");
            var aggFinalPath = Path.Combine(statusDir, aggFileName);
            var aggJson = JsonSerializer.Serialize(combined, _jsonOptions);
            await File.WriteAllTextAsync(aggTempPath, aggJson);
            File.Move(aggTempPath, aggFinalPath, overwrite: true);
            _logger.LogDebug("Saved aggregated status JSON for HTTP: {FilePath}", aggFinalPath);
        }
    }

    /// <summary>
    /// Copies status JSON files to the central output path for aggregation by central generator
    /// </summary>
    private async Task CopyStatusToCentralPathAsync(List<RuntimeStatus> statuses, string currentHostname)
    {
        var centralPath = _config.Output?.CentralOutputPath;
        if (string.IsNullOrEmpty(centralPath))
        {
            return;
        }

        try
        {
            // Create central directory if it doesn't exist
            Directory.CreateDirectory(centralPath);

            // Write a single combined status file for this server
            var combinedStatus = new CentralServerStatus
            {
                Hostname = currentHostname,
                GeneratedAt = DateTime.UtcNow,
                Statuses = statuses.Select(s => new RuntimeStatus
                {
                    Server = s.Server,
                    Service = s.Service,
                    LastUpdate = s.LastUpdate,
                Status = s.Status,
                StatusMessage = s.StatusMessage,
                Metrics = s.Metrics,
                Endpoints = s.Endpoints,
                History = new List<MetricSnapshot>(), // Don't include history in central status
                AppPools = s.AppPools,
                Services = s.Services,
                EventLogs = s.EventLogs,
                SqlQueryChecks = s.SqlQueryChecks,
                EmailProbes = s.EmailProbes,
                EmailDelivery = s.EmailDelivery,
                Certificates = s.Certificates,
                BizTalk = s.BizTalk,
                FileMonitoring = s.FileMonitoring
                }).ToList()
            };

            var fileName = $"{currentHostname}.status.json";
            var tempPath = Path.Combine(centralPath, $"{fileName}.tmp");
            var finalPath = Path.Combine(centralPath, fileName);

            // Write to temp file first, then rename (atomic operation)
            var json = JsonSerializer.Serialize(combinedStatus, _jsonOptions);
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);

            _logger.LogInformation("Copied status to central path: {FilePath} ({StatusCount} service(s))", 
                finalPath, statuses.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy status to central path: {CentralPath}", centralPath);
        }
    }

    /// <summary>
    /// Reads all status files from the central path
    /// </summary>
    private async Task<List<CentralServerStatus>> ReadStatusesFromCentralPathAsync()
    {
        var centralPath = _config.Output?.CentralGenerator?.CentralStatusPath;
        if (string.IsNullOrEmpty(centralPath) || !Directory.Exists(centralPath))
        {
            _logger.LogWarning("Central status path not configured or doesn't exist: {Path}", centralPath);
            return new List<CentralServerStatus>();
        }

        var filePattern = _config.Output.CentralGenerator.FilePattern ?? "*.status.json";
        var staleThresholdMinutes = _config.Output.CentralGenerator.StaleThresholdMinutes;
        var staleThreshold = DateTime.UtcNow.AddMinutes(-staleThresholdMinutes);

        var allStatuses = new List<CentralServerStatus>();
        var files = Directory.GetFiles(centralPath, filePattern);

        _logger.LogInformation("Reading {FileCount} status file(s) from central path: {Path}", files.Length, centralPath);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var serverStatus = JsonSerializer.Deserialize<CentralServerStatus>(json, _jsonOptions);
                
                if (serverStatus != null)
                {
                    // Check if status is stale
                    if (serverStatus.GeneratedAt < staleThreshold)
                    {
                        _logger.LogDebug("Status file is stale: {File} (generated at {GeneratedAt})", 
                            file, serverStatus.GeneratedAt);
                        // Mark all statuses as Unknown
                        foreach (var status in serverStatus.Statuses)
                        {
                            status.Status = ServiceStatus.Unknown;
                            status.StatusMessage = $"Status veraltet (älter als {staleThresholdMinutes} Minuten)";
                        }
                    }
                    allStatuses.Add(serverStatus);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read status file: {File}", file);
            }
        }

        return allStatuses;
    }

    /// <summary>
    /// Generates the central overview HTML page from all server status files
    /// </summary>
    private async Task GenerateCentralOverviewFromFilesAsync()
    {
        var serverStatuses = await ReadStatusesFromCentralPathAsync();
        if (serverStatuses.Count == 0)
        {
            _logger.LogWarning("No status files found for central overview generation");
            return;
        }

        // Flatten all statuses
        var allStatuses = serverStatuses.SelectMany(s => s.Statuses).ToList();

        // Determine output path
        var outputPath = _config.Output?.CentralGenerator?.OutputPath ?? _wwwrootDirectory;
        Directory.CreateDirectory(outputPath);

        // Generate the central overview HTML
        var html = GenerateCentralOverviewHtml(serverStatuses, allStatuses);

        var filePath = Path.Combine(outputPath, "central.html");
        await File.WriteAllTextAsync(filePath, html);

        // Also generate an index.html if outputPath is different from wwwroot
        if (!string.Equals(outputPath, _wwwrootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var indexPath = Path.Combine(outputPath, "index.html");
            await File.WriteAllTextAsync(indexPath, html);
        }

        _logger.LogInformation("Generated central overview: {FilePath} ({ServerCount} server(s), {StatusCount} service(s))",
            filePath, serverStatuses.Count, allStatuses.Count);
    }

    /// <summary>
    /// Generates the HTML for the central overview page
    /// </summary>
    private string GenerateCentralOverviewHtml(List<CentralServerStatus> serverStatuses, List<RuntimeStatus> allStatuses)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"de\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"60\">");
        html.AppendLine("    <title>Minicon.SimpleAdmin - Zentrale Übersicht</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetCommonStyles());
        html.AppendLine(GetCentralOverviewStyles());
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        // Header with summary
        var healthyCount = allStatuses.Count(s => s.Status == ServiceStatus.Healthy);
        var degradedCount = allStatuses.Count(s => s.Status == ServiceStatus.Degraded);
        var unhealthyCount = allStatuses.Count(s => s.Status == ServiceStatus.Unhealthy);
        var unreachableCount = allStatuses.Count(s => s.Status == ServiceStatus.Unreachable);
        var unknownCount = allStatuses.Count(s => s.Status == ServiceStatus.Unknown);
        var acknowledgedCount = allStatuses.Count(s => s.Status == ServiceStatus.Acknowledged);

        html.AppendLine("    <header>");
        html.AppendLine("        <h1>🌐 Zentrale Status-Übersicht</h1>");
        html.AppendLine($"        <p>Stand: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC (Auto-Refresh: 60s)</p>");
        html.AppendLine("        <div class=\"summary-bar\">");
        if (healthyCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count healthy\">{healthyCount}</span> Healthy</div>");
        if (degradedCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count degraded\">{degradedCount}</span> Degraded</div>");
        if (unhealthyCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count unhealthy\">{unhealthyCount}</span> Unhealthy</div>");
        if (unreachableCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count unreachable\">{unreachableCount}</span> Unreachable</div>");
        if (unknownCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count unknown\">{unknownCount}</span> Unknown</div>");
        if (acknowledgedCount > 0)
            html.AppendLine($"            <div class=\"summary-item\"><span class=\"summary-count acknowledged\">{acknowledgedCount}</span> Acknowledged</div>");
        html.AppendLine("        </div>");
        html.AppendLine("    </header>");
        html.AppendLine("    <main>");

        // Group by environment using config
        var statusesByEnvironment = new Dictionary<string, List<(string Hostname, RuntimeStatus Status)>>();
        var uncategorized = new List<(string Hostname, RuntimeStatus Status)>();

        foreach (var serverStatus in serverStatuses)
        {
            foreach (var status in serverStatus.Statuses)
            {
                var env = _config.GetEnvironmentForServer(status.Server);
                if (!string.IsNullOrEmpty(env))
                {
                    if (!statusesByEnvironment.ContainsKey(env))
                        statusesByEnvironment[env] = new List<(string, RuntimeStatus)>();
                    statusesByEnvironment[env].Add((serverStatus.Hostname, status));
                }
                else
                {
                    uncategorized.Add((serverStatus.Hostname, status));
                }
            }
        }

        // Define environment order and colors
        var environmentOrder = new[] { "production", "ref", "development" };
        var environmentColors = new Dictionary<string, (string header, string icon)>
        {
            ["production"] = ("#c0392b", "🔴"),
            ["ref"] = ("#d35400", "🟠"),
            ["development"] = ("#27ae60", "🟢")
        };

        // Render environments in order
        foreach (var envName in environmentOrder)
        {
            if (!statusesByEnvironment.ContainsKey(envName))
                continue;

            var statuses = statusesByEnvironment[envName];
            var (headerColor, icon) = environmentColors.GetValueOrDefault(envName, ("#34495e", "⚪"));

            html.AppendLine($"        <section class=\"environment-section\">");
            html.AppendLine($"            <div class=\"environment-header\" style=\"background: {headerColor};\">");
            html.AppendLine($"                {icon} {envName.ToUpperInvariant()}");
            html.AppendLine($"                <span class=\"env-count\">({statuses.Count} Service(s))</span>");
            html.AppendLine($"            </div>");
            html.AppendLine($"            <div class=\"grid\">");

            foreach (var (hostname, status) in statuses.OrderBy(s => s.Status.Server).ThenBy(s => s.Status.Service))
            {
                html.AppendLine(GenerateStatusCard(hostname, status));
            }

            html.AppendLine($"            </div>");
            html.AppendLine($"        </section>");
        }

        // Render any uncategorized statuses
        if (uncategorized.Count > 0)
        {
            html.AppendLine($"        <section class=\"environment-section\">");
            html.AppendLine($"            <div class=\"environment-header\" style=\"background: #7f8c8d;\">");
            html.AppendLine($"                ⚪ SONSTIGE");
            html.AppendLine($"                <span class=\"env-count\">({uncategorized.Count} Service(s))</span>");
            html.AppendLine($"            </div>");
            html.AppendLine($"            <div class=\"grid\">");

            foreach (var (hostname, status) in uncategorized.OrderBy(s => s.Status.Server).ThenBy(s => s.Status.Service))
            {
                html.AppendLine(GenerateStatusCard(hostname, status));
            }

            html.AppendLine($"            </div>");
            html.AppendLine($"        </section>");
        }

        // Server list with last update times
        html.AppendLine($"        <section class=\"server-info-section\">");
        html.AppendLine($"            <h2>📡 Server-Status</h2>");
        html.AppendLine($"            <div class=\"server-list\">");
        foreach (var serverStatus in serverStatuses.OrderBy(s => s.Hostname))
        {
            var ageMinutes = (DateTime.UtcNow - serverStatus.GeneratedAt).TotalMinutes;
            var ageClass = ageMinutes > 5 ? "stale" : ageMinutes > 2 ? "warning" : "fresh";
            html.AppendLine($"                <div class=\"server-item {ageClass}\">");
            html.AppendLine($"                    <span class=\"server-name\">{serverStatus.Hostname}</span>");
            html.AppendLine($"                    <span class=\"server-time\">{serverStatus.GeneratedAt:HH:mm:ss} ({ageMinutes:F0} min)</span>");
            html.AppendLine($"                    <span class=\"server-count\">{serverStatus.Statuses.Count} Service(s)</span>");
            html.AppendLine($"                </div>");
        }
        html.AppendLine($"            </div>");
        html.AppendLine($"        </section>");

        html.AppendLine("    </main>");
        html.AppendLine("    <footer>");
        html.AppendLine($"        <p>Generiert von SimpleAdmin Central Generator | {serverStatuses.Count} Server | {allStatuses.Count} Services</p>");
        html.AppendLine("    </footer>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    /// <summary>
    /// Generates a status card for the central overview
    /// </summary>
    private string GenerateStatusCard(string hostname, RuntimeStatus status)
    {
        var statusClass = status.Status.ToString().ToLower();
        var statusIcon = GetStatusIcon(status.Status);

        var card = new StringBuilder();
        card.AppendLine($"                <div class=\"card card-{statusClass}\">");
        card.AppendLine($"                    <div class=\"card-header\">");
        card.AppendLine($"                        <h3>{statusIcon} {status.Server}</h3>");
        card.AppendLine($"                        <span class=\"status-badge status-{statusClass}\">{status.Status}</span>");
        card.AppendLine($"                    </div>");
        card.AppendLine($"                    <div class=\"card-body\">");
        card.AppendLine($"                        <p class=\"service-name\">{status.Service}</p>");
        card.AppendLine($"                        <p class=\"timestamp\">Update: {status.LastUpdate:HH:mm:ss}</p>");
        if (!string.IsNullOrEmpty(status.StatusMessage))
        {
            card.AppendLine($"                        <p class=\"status-message\">{status.StatusMessage}</p>");
        }

        // Show key metrics
        if (status.Metrics?.Count > 0)
        {
            card.AppendLine("                        <div class=\"metrics\">");
            foreach (var metric in status.Metrics.Take(3))
            {
                var metricClass = metric.Status.ToString().ToLower();
                card.AppendLine($"                            <div class=\"metric metric-{metricClass}\">");
                card.AppendLine($"                                <span class=\"metric-name\">{metric.DisplayName}:</span>");
                card.AppendLine($"                                <span class=\"metric-value\">{metric.Value:F1}{metric.Unit}</span>");
                card.AppendLine($"                            </div>");
            }
            card.AppendLine("                        </div>");
        }

        card.AppendLine($"                    </div>");
        card.AppendLine($"                </div>");

        return card.ToString();
    }

    /// <summary>
    /// Returns CSS styles specific to the central overview page
    /// </summary>
    private string GetCentralOverviewStyles()
    {
        return @"
            .summary-bar {
                display: flex;
                gap: 1.5rem;
                justify-content: center;
                flex-wrap: wrap;
                margin-top: 1rem;
            }
            .summary-item {
                text-align: center;
                color: white;
            }
            .summary-count {
                display: inline-block;
                padding: 0.25rem 0.75rem;
                border-radius: 4px;
                font-weight: bold;
                font-size: 1.25rem;
                margin-right: 0.5rem;
            }
            .summary-count.healthy { background: #27ae60; }
            .summary-count.degraded { background: #f39c12; }
            .summary-count.unhealthy { background: #e74c3c; }
            .summary-count.unreachable { background: #34495e; }
            .summary-count.unknown { background: #95a5a6; }
            .summary-count.acknowledged { background: #9b59b6; }
            .environment-section { margin-bottom: 2rem; }
            .environment-header {
                color: white;
                padding: 0.75rem 1rem;
                margin-bottom: 1rem;
                border-radius: 6px;
                font-size: 1.1rem;
                font-weight: bold;
                display: flex;
                justify-content: space-between;
                align-items: center;
            }
            .env-count { font-weight: normal; font-size: 0.9rem; opacity: 0.9; }
            .service-name { font-weight: 600; color: #2c3e50; margin-bottom: 0.5rem; }
            .card h3 { font-size: 1rem; margin: 0; }
            .server-info-section { margin-top: 2rem; padding: 1rem; background: white; border-radius: 8px; }
            .server-info-section h2 { margin-bottom: 1rem; color: #2c3e50; }
            .server-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 0.75rem; }
            .server-item { 
                display: flex; 
                justify-content: space-between; 
                align-items: center;
                padding: 0.5rem 0.75rem; 
                background: #f8f9fa; 
                border-radius: 4px;
                border-left: 3px solid #27ae60;
            }
            .server-item.warning { border-left-color: #f39c12; }
            .server-item.stale { border-left-color: #e74c3c; background: #fdf2f2; }
            .server-name { font-weight: 600; }
            .server-time { font-size: 0.85rem; color: #7f8c8d; }
            .server-count { font-size: 0.85rem; color: #95a5a6; }
            footer { text-align: center; padding: 1rem; color: #7f8c8d; font-size: 0.9rem; }
        ";
    }

    /// <summary>
    /// Fetches status from a remote server via HTTP with DNS check first
    /// </summary>
    private async Task<List<RuntimeStatus>> FetchStatusFromServerWithDnsCheckAsync(Server server)
    {
        // Quick DNS check before attempting HTTP
        var hostname = GetHostnameFromUrl(server.BaseUrl);
        if (hostname != null && !await IsHostReachableAsync(hostname))
        {
            _logger.LogDebug("Server {ServerName} ({Hostname}) not reachable via DNS - returning Unreachable status", server.Name, hostname);
            return CreateUnreachableStatusesForServer(server, "DNS nicht auflösbar");
        }

        return await FetchStatusFromServerAsync(server);
    }

    /// <summary>
    /// Creates Unreachable status entries for all service types of a server
    /// </summary>
    private List<RuntimeStatus> CreateUnreachableStatusesForServer(Server server, string reason)
    {
        var statuses = new List<RuntimeStatus>();

        foreach (var serviceType in server.ServiceTypes)
        {
            statuses.Add(new RuntimeStatus
            {
                Server = server.Name,
                Service = serviceType.Type,
                LastUpdate = DateTime.UtcNow,
                Status = ServiceStatus.Unreachable,
                Metrics = new List<Metric>(),
                Endpoints = new List<EndpointStatus>(),
                History = new List<MetricSnapshot>(),
                StatusMessage = reason
            });
        }

        return statuses;
    }

    /// <summary>
    /// Fetches status from a remote server via HTTP
    /// </summary>
    private async Task<List<RuntimeStatus>> FetchStatusFromServerAsync(Server server)
    {
        var statuses = new List<RuntimeStatus>();

        if (string.IsNullOrEmpty(server.BaseUrl))
        {
            _logger.LogWarning("Server {ServerName} has no baseUrl configured - returning Unreachable status", server.Name);
            return CreateUnreachableStatusesForServer(server, "Keine BaseUrl konfiguriert");
        }

        foreach (var serviceType in server.ServiceTypes)
        {
            // Use HTTP for internal status fetching (not HTTPS)
            var baseUrl = server.BaseUrl.TrimEnd('/').Replace("https://", "http://");
            var statusUrl = $"{baseUrl}/status/{server.Name}.{serviceType.Type}.json";

            try
            {
                _logger.LogDebug("Fetching status from {Url}", statusUrl);
                var response = await _httpClient.GetAsync(statusUrl);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var status = JsonSerializer.Deserialize<RuntimeStatus>(json, _jsonOptions);
                    if (status != null)
                    {
                        statuses.Add(status);
                        _logger.LogDebug("Successfully fetched status for {Server}.{Service}", server.Name, serviceType.Type);
                    }
                }
                else
                {
                    _logger.LogDebug("Failed to fetch status from {Url}: {StatusCode} - adding Unreachable status", statusUrl, response.StatusCode);
                    statuses.Add(CreateUnreachableStatusForService(server.Name, serviceType.Type, $"HTTP {(int)response.StatusCode}"));
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Timeout fetching status from {Url} - adding Unreachable status", statusUrl);
                statuses.Add(CreateUnreachableStatusForService(server.Name, serviceType.Type, "Timeout"));
            }
            catch (HttpRequestException)
            {
                _logger.LogDebug("Cannot connect to {Url} - adding Unreachable status", statusUrl);
                statuses.Add(CreateUnreachableStatusForService(server.Name, serviceType.Type, "Verbindung fehlgeschlagen"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error fetching status from {Url} - adding Unreachable status", statusUrl);
                statuses.Add(CreateUnreachableStatusForService(server.Name, serviceType.Type, "Fehler: " + ex.Message));
            }
        }

        return statuses;
    }

    /// <summary>
    /// Creates an Unreachable status entry for a single service
    /// </summary>
    private RuntimeStatus CreateUnreachableStatusForService(string serverName, string serviceType, string reason)
    {
        return new RuntimeStatus
        {
            Server = serverName,
            Service = serviceType,
            LastUpdate = DateTime.UtcNow,
            Status = ServiceStatus.Unreachable,
            Metrics = new List<Metric>(),
            Endpoints = new List<EndpointStatus>(),
            History = new List<MetricSnapshot>(),
            StatusMessage = reason
        };
    }

    /// <summary>
    /// Checks if a hostname is resolvable via DNS
    /// </summary>
    private async Task<bool> IsHostReachableAsync(string hostname)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(hostname);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts hostname from a URL
    /// </summary>
    private string? GetHostnameFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates the all.html page that aggregates status from all environments and reachable servers
    /// </summary>
    private async Task GenerateAllServersPageAsync(Config config, string currentHostname)
    {
        _logger.LogInformation("Generating all-servers overview page for all environments...");

        // Collect all servers with their environment info for parallel processing
        var serverTasks = new List<(string Environment, Server Server, Task<List<RuntimeStatus>> Task)>();

        foreach (var envEntry in config.Environments)
        {
            var environmentName = envEntry.Key;
            var serversInEnvironment = config.GetServersInEnvironment(environmentName)
                .Where(s => s.Active)
                .ToList();

            _logger.LogInformation("Queuing environment '{Environment}': {ServerCount} active server(s)",
                environmentName, serversInEnvironment.Count);

            foreach (var server in serversInEnvironment)
            {
                // Start all fetch tasks in parallel
                var task = FetchStatusFromServerWithDnsCheckAsync(server);
                serverTasks.Add((environmentName, server, task));
            }
        }

        _logger.LogInformation("Fetching status from {ServerCount} server(s) in parallel...", serverTasks.Count);

        // Wait for all tasks to complete
        await Task.WhenAll(serverTasks.Select(t => t.Task));

        // Collect results by environment
        var statusesByEnvironment = new Dictionary<string, List<RuntimeStatus>>();
        var totalReachable = 0;

        foreach (var (environment, server, task) in serverTasks)
        {
            var statuses = await task; // Already completed, just getting result
            if (statuses.Count > 0)
            {
                totalReachable++;
                if (!statusesByEnvironment.ContainsKey(environment))
                {
                    statusesByEnvironment[environment] = new List<RuntimeStatus>();
                }
                statusesByEnvironment[environment].AddRange(statuses);
            }
        }

        _logger.LogInformation("Fetched statuses from {ReachableCount}/{TotalCount} reachable server(s) across {EnvCount} environment(s)",
            totalReachable, serverTasks.Count, statusesByEnvironment.Count);

        // Collect all statuses for summary
        var allStatuses = statusesByEnvironment.Values.SelectMany(s => s).ToList();

        // Generate the HTML page
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"de\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"UTF-8\">");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine("    <meta http-equiv=\"refresh\" content=\"60\">");
        html.AppendLine("    <title>Minicon.SimpleAdmin - All Environments Overview</title>");
        html.AppendLine("    <style>");
        html.AppendLine(GetCommonStyles());
        html.AppendLine(@"
        .server-group { margin-bottom: 2rem; }
        .server-group h2 {
            background: #34495e;
            color: white;
            padding: 1rem;
            margin-bottom: 1rem;
            border-radius: 4px;
        }
        .environment-section { margin-bottom: 1.5rem; }
        .environment-header {
            background: #2c3e50;
            color: white;
            padding: 0.75rem 1rem;
            margin-bottom: 0.75rem;
            border-radius: 6px;
            font-size: 1.1rem;
        }
        .environment-header.production { background: #c0392b; }
        .environment-header.ref { background: #d35400; }
        .environment-header.development { background: #27ae60; }
        .summary-bar {
            display: flex;
            gap: 1.5rem;
            justify-content: center;
            flex-wrap: wrap;
            padding: 0.75rem;
            background: white;
            border-radius: 8px;
            margin-bottom: 1.5rem;
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }
        .summary-item {
            text-align: center;
            padding: 0.25rem 1rem;
        }
        .summary-count {
            font-size: 1.5rem;
            font-weight: bold;
        }
        .summary-label {
            font-size: 0.8rem;
            color: #7f8c8d;
        }
        /* Compact grid for all.html */
        .compact-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
            gap: 0.5rem;
        }
        .compact-card {
            background: white;
            border-radius: 6px;
            box-shadow: 0 1px 3px rgba(0,0,0,0.1);
            padding: 0.5rem 0.75rem;
            display: flex;
            align-items: center;
            gap: 0.5rem;
            border-left: 3px solid #95a5a6;
        }
        .compact-card.card-healthy { border-left-color: #27ae60; }
        .compact-card.card-degraded { border-left-color: #f39c12; }
        .compact-card.card-unhealthy { border-left-color: #e74c3c; }
        .compact-card.card-unknown { border-left-color: #95a5a6; }
        .compact-card.card-unreachable { border-left-color: #34495e; background: #f8f8f8; }
        .compact-icon { font-size: 1rem; flex-shrink: 0; }
        .compact-info { flex: 1; min-width: 0; overflow: hidden; }
        .compact-server { font-size: 0.75rem; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .compact-service { font-size: 0.65rem; color: #7f8c8d; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .compact-status { font-size: 0.6rem; padding: 0.15rem 0.4rem; border-radius: 3px; font-weight: bold; flex-shrink: 0; }
        a.compact-card { text-decoration: none; color: inherit; transition: transform 0.1s, box-shadow 0.1s; }
        a.compact-card:hover { transform: translateY(-1px); box-shadow: 0 2px 6px rgba(0,0,0,0.15); }
        /* View toggle per environment */
        .env-header-row { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
        .env-header-row .environment-header { flex: 1; margin-bottom: 0; }
        .view-toggle { display: flex; gap: 0.25rem; }
        .view-toggle button { padding: 0.4rem 0.8rem; border: none; background: rgba(255,255,255,0.15); color: #555; border-radius: 4px; cursor: pointer; font-size: 0.8rem; transition: all 0.2s; }
        .view-toggle button:hover { background: rgba(255,255,255,0.4); color: #333; }
        .view-toggle button.active { background: rgba(255,255,255,0.95); color: #2c3e50; font-weight: bold; }
        /* Normal view grid */
        .normal-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(350px, 1fr)); gap: 1.5rem; }
        /* View visibility */
        .env-compact-view, .env-normal-view { display: none; }
        .env-compact-view.active, .env-normal-view.active { display: block; }
        ");
        html.AppendLine("    </style>");

        // Add JavaScript for view toggle and cookie handling
        html.AppendLine(@"
    <script>
        function getCookie(name) {
            const value = `; ${document.cookie}`;
            const parts = value.split(`; ${name}=`);
            if (parts.length === 2) return parts.pop().split(';').shift();
            return null;
        }

        function setCookie(name, value, days = 365) {
            const expires = new Date(Date.now() + days * 864e5).toUTCString();
            document.cookie = `${name}=${value}; expires=${expires}; path=/; SameSite=Lax`;
        }

        function setView(env, view) {
            const section = document.getElementById(`env-${env}`);
            if (!section) return;

            const compactView = section.querySelector('.env-compact-view');
            const normalView = section.querySelector('.env-normal-view');
            const compactBtn = section.querySelector('.btn-compact');
            const normalBtn = section.querySelector('.btn-normal');

            if (view === 'compact') {
                compactView?.classList.add('active');
                normalView?.classList.remove('active');
                compactBtn?.classList.add('active');
                normalBtn?.classList.remove('active');
            } else {
                compactView?.classList.remove('active');
                normalView?.classList.add('active');
                compactBtn?.classList.remove('active');
                normalBtn?.classList.add('active');
            }

            setCookie(`view_${env}`, view);
        }

        // Initialize views from cookies on page load
        document.addEventListener('DOMContentLoaded', function() {
            document.querySelectorAll('.environment-section').forEach(section => {
                const env = section.id.replace('env-', '');
                const savedView = getCookie(`view_${env}`) || 'compact';
                setView(env, savedView);
            });
        });
    </script>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <header>");
        html.AppendLine("        <h1>All Environments Overview</h1>");
        html.AppendLine($"        <p>Last updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC (auto-refresh: 60s)</p>");
        html.AppendLine($"        <p>Current host: {currentHostname}</p>");
        html.AppendLine("        <p><a href=\"./documentation.html\" style=\"color: #ecf0f1; text-decoration: underline;\">Documentation</a> | <a href=\"./\" style=\"color: #ecf0f1; text-decoration: underline;\">Local Status</a></p>");
        html.AppendLine("    </header>");
        html.AppendLine("    <main>");

        // Summary bar
        var healthyCount = allStatuses.Count(s => s.Status == ServiceStatus.Healthy);
        var degradedCount = allStatuses.Count(s => s.Status == ServiceStatus.Degraded);
        var unhealthyCount = allStatuses.Count(s => s.Status == ServiceStatus.Unhealthy);
        var unknownCount = allStatuses.Count(s => s.Status == ServiceStatus.Unknown);
        var unreachableCount = allStatuses.Count(s => s.Status == ServiceStatus.Unreachable);

        html.AppendLine("        <div class=\"summary-bar\">");
        html.AppendLine($"            <div class=\"summary-item\"><div class=\"summary-count\" style=\"color: #27ae60;\">{healthyCount}</div><div class=\"summary-label\">Healthy</div></div>");
        html.AppendLine($"            <div class=\"summary-item\"><div class=\"summary-count\" style=\"color: #f39c12;\">{degradedCount}</div><div class=\"summary-label\">Degraded</div></div>");
        html.AppendLine($"            <div class=\"summary-item\"><div class=\"summary-count\" style=\"color: #e74c3c;\">{unhealthyCount}</div><div class=\"summary-label\">Unhealthy</div></div>");
        html.AppendLine($"            <div class=\"summary-item\"><div class=\"summary-count\" style=\"color: #34495e;\">{unreachableCount}</div><div class=\"summary-label\">Unreachable</div></div>");
        html.AppendLine($"            <div class=\"summary-item\"><div class=\"summary-count\" style=\"color: #95a5a6;\">{unknownCount}</div><div class=\"summary-label\">Unknown</div></div>");
        html.AppendLine("        </div>");

        // Order environments: production, ref, development (case-insensitive)
        var orderedEnvironments = config.Environments.Keys
            .OrderBy(e => e.ToLowerInvariant() switch
            {
                "production" => 0,
                "ref" => 1,
                "development" => 2,
                _ => 3
            })
            .ToList();

        foreach (var environmentName in orderedEnvironments)
        {
            var envStatuses = statusesByEnvironment.GetValueOrDefault(environmentName, new List<RuntimeStatus>());

            // Skip environments with no reachable servers
            if (envStatuses.Count == 0)
            {
                _logger.LogDebug("Skipping environment '{Environment}' - no servers reachable", environmentName);
                continue;
            }

            var envHeaderClass = environmentName.ToLowerInvariant() switch
            {
                "production" => "production",
                "ref" => "ref",
                "development" => "development",
                _ => ""
            };

            var envDisplayName = environmentName.ToUpperInvariant();
            var envKey = environmentName.ToLowerInvariant();

            html.AppendLine($"        <div id=\"env-{envKey}\" class=\"environment-section\">");
            html.AppendLine($"            <div class=\"env-header-row\">");
            html.AppendLine($"                <div class=\"environment-header {envHeaderClass}\">{envDisplayName} ({envStatuses.Count} services)</div>");
            html.AppendLine($"                <div class=\"view-toggle\">");
            html.AppendLine($"                    <button class=\"btn-compact\" onclick=\"setView('{envKey}', 'compact')\">Compact</button>");
            html.AppendLine($"                    <button class=\"btn-normal\" onclick=\"setView('{envKey}', 'normal')\">Normal</button>");
            html.AppendLine($"                </div>");
            html.AppendLine($"            </div>");

            if (envStatuses.Count > 0)
            {
                // Compact view
                html.AppendLine("            <div class=\"env-compact-view\">");
                html.AppendLine("                <div class=\"compact-grid\">");

                foreach (var status in envStatuses.OrderBy(s => s.Server).ThenBy(s => s.Service))
                {
                    var statusClass = status.Status.ToString().ToLower();
                    var statusIcon = GetStatusIcon(status.Status);
                    var serverConfig = config.Servers.FirstOrDefault(s => s.Name.Equals(status.Server, StringComparison.OrdinalIgnoreCase));
                    var serverUrl = serverConfig?.BaseUrl ?? "#";
                    var detailsUrl = serverUrl.TrimEnd('/') + $"/{status.Server}.{status.Service}.html";
                    var tooltip = !string.IsNullOrEmpty(status.StatusMessage)
                        ? $"{status.Status}: {status.StatusMessage}"
                        : $"{status.Status} - Last update: {status.LastUpdate:HH:mm:ss}";

                    html.AppendLine($"                    <a href=\"{detailsUrl}\" target=\"_blank\" class=\"compact-card card-{statusClass}\" title=\"{tooltip}\">");
                    html.AppendLine($"                        <span class=\"compact-icon\">{statusIcon}</span>");
                    html.AppendLine($"                        <div class=\"compact-info\">");
                    html.AppendLine($"                            <div class=\"compact-server\" title=\"{status.Server}\">{status.Server}</div>");
                    html.AppendLine($"                            <div class=\"compact-service\" title=\"{status.Service}\">{status.Service}</div>");
                    html.AppendLine($"                        </div>");
                    html.AppendLine($"                        <span class=\"compact-status status-{statusClass}\">{status.Status}</span>");
                    html.AppendLine($"                    </a>");
                }

                html.AppendLine("                </div>");
                html.AppendLine("            </div>");

                // Normal view
                html.AppendLine("            <div class=\"env-normal-view\">");
                html.AppendLine("                <div class=\"normal-grid\">");

                foreach (var status in envStatuses.OrderBy(s => s.Server).ThenBy(s => s.Service))
                {
                    var statusClass = status.Status.ToString().ToLower();
                    var statusIcon = GetStatusIcon(status.Status);
                    var serverConfig = config.Servers.FirstOrDefault(s => s.Name.Equals(status.Server, StringComparison.OrdinalIgnoreCase));
                    var serverUrl = serverConfig?.BaseUrl ?? "#";
                    var detailsUrl = serverUrl.TrimEnd('/') + $"/{status.Server}.{status.Service}.html";

                    html.AppendLine($"                    <div class=\"card card-{statusClass}\">");
                    html.AppendLine($"                        <div class=\"card-header\">");
                    html.AppendLine($"                            <h2>{statusIcon} {status.Server} - {status.Service}</h2>");
                    html.AppendLine($"                            <span class=\"status-badge status-{statusClass}\">{status.Status}</span>");
                    html.AppendLine($"                        </div>");
                    html.AppendLine($"                        <div class=\"card-body\">");
                    html.AppendLine($"                            <p class=\"timestamp\">Last update: {status.LastUpdate:yyyy-MM-dd HH:mm:ss}</p>");

                    if (!string.IsNullOrEmpty(status.StatusMessage))
                    {
                        html.AppendLine($"                            <p class=\"status-message\">⚡ {status.StatusMessage}</p>");
                    }

                    if (status.Metrics.Count > 0)
                    {
                        html.AppendLine("                            <div class=\"metrics\">");
                        foreach (var metric in status.Metrics.Take(3))
                        {
                            var metricClass = metric.Status.ToString().ToLower();
                            html.AppendLine($"                                <div class=\"metric metric-{metricClass}\">");
                            html.AppendLine($"                                    <span class=\"metric-name\">{metric.DisplayName}:</span>");
                            html.AppendLine($"                                    <span class=\"metric-value\">{metric.Value:F1}{metric.Unit}</span>");
                            html.AppendLine($"                                </div>");
                        }
                        html.AppendLine("                            </div>");
                    }

                    html.AppendLine($"                            <div class=\"links\">");
                    html.AppendLine($"                                <a href=\"{detailsUrl}\" class=\"link link-details\" target=\"_blank\">Details</a>");
                    html.AppendLine($"                                <a href=\"{serverUrl}\" class=\"link\" target=\"_blank\">Server</a>");
                    html.AppendLine($"                            </div>");
                    html.AppendLine($"                        </div>");
                    html.AppendLine($"                    </div>");
                }

                html.AppendLine("                </div>");
                html.AppendLine("            </div>");
            }

            html.AppendLine("        </div>");
        }

        html.AppendLine("    </main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var filePath = Path.Combine(_wwwrootDirectory, "all.html");
        await File.WriteAllTextAsync(filePath, html.ToString());
        _logger.LogInformation("Generated all-servers page: {FilePath}", filePath);
    }

    /// <summary>
    /// Generates monitoring badges HTML (AppPools, Windows Services, SQL Checks, EventLog)
    /// </summary>
    private string GenerateMonitoringBadgesHtml(RuntimeStatus status, int indentLevel = 5)
    {
        int totalPools = status.AppPools?.Count ?? 0;
        int unhealthyPools = status.AppPools?.Values.Count(p => !p.IsHealthy) ?? 0;
        int stoppedPools = status.AppPools?.Values.Count(p => p.Status != Minicon.SimpleAdmin.Models.State.AppPoolStatus.Running) ?? 0;

        int totalServices = status.Services?.Count ?? 0;
        int stoppedServices = status.Services?.Values.Count(s => s.Status != "Running") ?? 0;

        int sqlTotal = status.SqlQueryChecks?.Count ?? 0;
        int sqlCritical = status.SqlQueryChecks?.Values.Count(c => c.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical) ?? 0;

        bool hasEventLog = status.EventLogs != null;

        int emailProbeTotal = status.EmailProbes?.Count ?? 0;
        int emailProbeCritical = status.EmailProbes?.Values.Count(p => p.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical) ?? 0;

        int emailDeliveryTotal = status.EmailDelivery?.Count ?? 0;
        int emailDeliveryCritical = status.EmailDelivery?.Values.Count(c => c.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical) ?? 0;
        int emailDeliveryWarning = status.EmailDelivery?.Values.Count(c => c.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning) ?? 0;

        int certTotal = status.Certificates?.Count ?? 0;
        int certCritical = status.Certificates?.Values.Count(c => c.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical) ?? 0;
        int certWarning = status.Certificates?.Values.Count(c => c.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning) ?? 0;

        var bt = status.BizTalk;
        var fm = status.FileMonitoring;

        if (totalPools == 0 && totalServices == 0 && sqlTotal == 0 && !hasEventLog && emailProbeTotal == 0 && emailDeliveryTotal == 0 && certTotal == 0 && bt == null && fm == null)
            return string.Empty;

        var indent = new string(' ', indentLevel * 4);
        var innerIndent = new string(' ', (indentLevel + 1) * 4);
        var sb = new StringBuilder();
        sb.AppendLine($"{indent}<div class=\"monitoring-badges\" style=\"display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px;\">");

        if (totalPools > 0)
        {
            var cls = stoppedPools > 0 ? "badge-critical" : unhealthyPools > 0 ? "badge-warning" : "badge-ok";
            var healthyCount = totalPools - unhealthyPools;
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"AppPools: {totalPools - stoppedPools}/{totalPools} running, {healthyCount}/{totalPools} healthy\">🔧 {healthyCount}/{totalPools} Pools</span>");
        }

        if (totalServices > 0)
        {
            var cls = stoppedServices > 0 ? "badge-critical" : "badge-ok";
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"Windows Services: {totalServices - stoppedServices}/{totalServices} running\">🪟 {totalServices - stoppedServices}/{totalServices} Svcs</span>");
        }

        if (sqlTotal > 0)
        {
            var cls = sqlCritical > 0 ? "badge-critical" : "badge-ok";
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"SQL Checks: {sqlTotal - sqlCritical}/{sqlTotal} ok\">🗄️ {sqlTotal - sqlCritical}/{sqlTotal} SQL</span>");
        }

        if (hasEventLog)
        {
            var cls = status.EventLogs!.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical ? "badge-critical"
                    : status.EventLogs.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning  ? "badge-warning"
                    : "badge-ok";
            var ignoredSuffix = status.EventLogs.IgnoredCount > 0 ? $" ({status.EventLogs.IgnoredCount} ignoriert)" : "";
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"Event Log: {status.EventLogs.EventCount} event(s), {status.EventLogs.IgnoredCount} ignored\">📋 {status.EventLogs.EventCount} Events{ignoredSuffix}</span>");
        }

        if (emailProbeTotal > 0)
        {
            var cls = emailProbeCritical > 0 ? "badge-critical" : "badge-ok";
            var ok = emailProbeTotal - emailProbeCritical;
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"E-Mail-Probes: {ok}/{emailProbeTotal} gesendet\">📧 {ok}/{emailProbeTotal} Probes</span>");
        }

        if (emailDeliveryTotal > 0)
        {
            var cls = emailDeliveryCritical > 0 ? "badge-critical" : emailDeliveryWarning > 0 ? "badge-warning" : "badge-ok";
            var ok = emailDeliveryTotal - emailDeliveryCritical - emailDeliveryWarning;
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"E-Mail-Zustellung: {ok}/{emailDeliveryTotal} ok\">📬 {ok}/{emailDeliveryTotal} Zustellung</span>");
        }

        if (certTotal > 0)
        {
            var cls = certCritical > 0 ? "badge-critical" : certWarning > 0 ? "badge-warning" : "badge-ok";
            var ok = certTotal - certCritical - certWarning;
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"Zertifikate: {ok}/{certTotal} ok\">🔒 {ok}/{certTotal} Zertifikate</span>");
        }

        if (bt != null)
        {
            if (!bt.ApiReachable)
            {
                sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge badge-critical\" title=\"BizTalk-Management-API nicht erreichbar\">⚙️ BizTalk API ✗</span>");
            }
            else
            {
                var btTotal = bt.ApplicationsTotal + bt.OrchestrationsTotal + bt.SendPortsTotal + bt.ReceiveLocationsTotal;
                var btCrit = bt.Problems.Count(p => p.IsCritical) + (bt.SuspendedStatus == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical ? 1 : 0);
                var btWarn = bt.Problems.Count(p => !p.IsCritical) + (bt.SuspendedStatus == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning ? 1 : 0);
                var cls = btCrit > 0 ? "badge-critical" : btWarn > 0 ? "badge-warning" : "badge-ok";
                var okCount = btTotal - bt.Problems.Count;
                sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"BizTalk: {okCount}/{btTotal} Artefakte ok, {bt.SuspendedTotal} suspendiert\">⚙️ {okCount}/{btTotal} BizTalk</span>");
            }
        }

        if (fm != null)
        {
            var fmFindings = fm.StuckFiles.Count + fm.LogMatches.Count;
            var fmCrit = fm.StuckFiles.Count(s => s.IsCritical) + fm.LogMatches.Count(m => m.IsCritical) + (fm.AnyError ? 1 : 0);
            var cls = fmCrit > 0 ? "badge-critical" : fmFindings > 0 ? "badge-warning" : "badge-ok";
            var label = fmFindings == 0 ? "OK" : $"{fmFindings} Fund(e)";
            sb.AppendLine($"{innerIndent}<span class=\"monitoring-badge {cls}\" title=\"Dateiüberwachung: {fm.DirectoriesChecked} Verzeichnis(se), {fm.LogFilesScanned} Log(s), {fm.StuckFiles.Count} hängend, {fm.LogMatches.Count} Log-Treffer\">📁 {label} Dateien</span>");
        }

        sb.AppendLine($"{indent}</div>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates HTML section for certificate status from RuntimeStatus.Certificates
    /// </summary>
    private string GenerateBizTalkSectionHtml(Minicon.SimpleAdmin.Models.State.BizTalkState bt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("                <h3 style=\"margin-top:24px;\">⚙️ BizTalk</h3>");

        if (!bt.ApiReachable)
        {
            sb.AppendLine($"                <p style=\"color:#b00;\">Management-API nicht erreichbar: {System.Net.WebUtility.HtmlEncode(bt.ApiError ?? "unbekannt")}</p>");
            return sb.ToString();
        }

        sb.AppendLine($"                <p style=\"color:#555;\">Anwendungen: {bt.ApplicationsTotal} · Orchestrierungen: {bt.OrchestrationsTotal} · Sendeports: {bt.SendPortsTotal} · Empfangsorte: {bt.ReceiveLocationsTotal} · Suspendiert: {bt.SuspendedTotal}</p>");

        if (bt.Problems.Count > 0)
        {
            sb.AppendLine("                <table style=\"width:100%; border-collapse:collapse;\">");
            sb.AppendLine("                    <thead>");
            sb.AppendLine("                        <tr style=\"background:#f8f9fa; text-align:left;\">");
            sb.AppendLine("                            <th style=\"padding:6px 10px;\">Typ</th>");
            sb.AppendLine("                            <th style=\"padding:6px 10px;\">Name</th>");
            sb.AppendLine("                            <th style=\"padding:6px 10px;\">Status</th>");
            sb.AppendLine("                            <th style=\"padding:6px 10px;\">Schweregrad</th>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                    </thead>");
            sb.AppendLine("                    <tbody>");
            foreach (var p in bt.Problems)
            {
                var cls = p.IsCritical ? "badge-critical" : "badge-warning";
                sb.AppendLine("                        <tr>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{System.Net.WebUtility.HtmlEncode(p.ArtifactType)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{System.Net.WebUtility.HtmlEncode(p.Name)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{System.Net.WebUtility.HtmlEncode(p.Status)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\"><span class=\"monitoring-badge {cls}\">{(p.IsCritical ? "Critical" : "Warning")}</span></td>");
                sb.AppendLine("                        </tr>");
            }
            sb.AppendLine("                    </tbody>");
            sb.AppendLine("                </table>");
        }

        if (bt.SuspendedGroups.Count > 0)
        {
            sb.AppendLine("                <p style=\"margin-top:12px; color:#555;\">Suspendierte Instanzen nach Anwendung:</p>");
            sb.AppendLine("                <ul>");
            foreach (var g in bt.SuspendedGroups)
                sb.AppendLine($"                    <li>{System.Net.WebUtility.HtmlEncode(g.Scope)}: {g.Count}</li>");
            sb.AppendLine("                </ul>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates the HTML section for file-share / log monitoring from RuntimeStatus.FileMonitoring
    /// </summary>
    private string GenerateFileMonitoringSectionHtml(Minicon.SimpleAdmin.Models.State.FileMonitoringState fm)
    {
        var sb = new StringBuilder();
        sb.AppendLine("                <h3 style=\"margin-top:24px;\">📁 Dateiüberwachung</h3>");

        if (fm.OutsideBusinessHours)
        {
            sb.AppendLine("                <p style=\"color:#555;\">Außerhalb der konfigurierten Betriebszeiten — keine Prüfung in diesem Zyklus.</p>");
            return sb.ToString();
        }

        sb.AppendLine($"                <p style=\"color:#555;\">Verzeichnisse geprüft: {fm.DirectoriesChecked} · Logdateien gescannt: {fm.LogFilesScanned} · Hängende Dateien: {fm.StuckFiles.Count} · Log-Treffer: {fm.LogMatches.Count}</p>");

        if (fm.AnyError && !string.IsNullOrEmpty(fm.Error))
        {
            sb.AppendLine($"                <p style=\"color:#b00;\">Zugriffsfehler: {System.Net.WebUtility.HtmlEncode(fm.Error)}</p>");
        }

        if (fm.StuckFiles.Count > 0)
        {
            sb.AppendLine("                <h4 style=\"margin-top:16px;\">Hängengebliebene Dateien</h4>");
            sb.AppendLine("                <table style=\"width:100%; border-collapse:collapse;\">");
            sb.AppendLine("                    <thead><tr style=\"background:#f8f9fa; text-align:left;\">");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Verzeichnis</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Datei</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Alter</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Grund</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Schweregrad</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Lösung</th>");
            sb.AppendLine("                    </tr></thead>");
            sb.AppendLine("                    <tbody>");
            foreach (var s in fm.StuckFiles)
            {
                var cls = s.IsCritical ? "badge-critical" : "badge-warning";
                var ageLabel = s.AgeMinutes >= 120 ? $"{s.AgeMinutes / 60.0:N1} h" : $"{s.AgeMinutes:N0} min";
                var reason = string.Equals(s.Reason, "cutoff", StringComparison.OrdinalIgnoreCase) ? "Nach Cutoff" : "Zu alt";
                sb.AppendLine("                        <tr>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{System.Net.WebUtility.HtmlEncode(s.Directory)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\" title=\"{System.Net.WebUtility.HtmlEncode(s.FilePath)}\">{System.Net.WebUtility.HtmlEncode(System.IO.Path.GetFileName(s.FilePath))}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{ageLabel}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{reason}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\"><span class=\"monitoring-badge {cls}\">{(s.IsCritical ? "Critical" : "Warning")}</span></td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{RenderWikiLink(s.WikiUrl)}</td>");
                sb.AppendLine("                        </tr>");
            }
            sb.AppendLine("                    </tbody>");
            sb.AppendLine("                </table>");
        }

        if (fm.LogMatches.Count > 0)
        {
            sb.AppendLine("                <h4 style=\"margin-top:16px;\">Log-Fehler</h4>");
            sb.AppendLine("                <table style=\"width:100%; border-collapse:collapse;\">");
            sb.AppendLine("                    <thead><tr style=\"background:#f8f9fa; text-align:left;\">");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Log</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Signatur</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Treffer</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Beispiel</th>");
            sb.AppendLine("                        <th style=\"padding:6px 10px;\">Lösung</th>");
            sb.AppendLine("                    </tr></thead>");
            sb.AppendLine("                    <tbody>");
            foreach (var m in fm.LogMatches)
            {
                var cls = m.IsCritical ? "badge-critical" : "badge-warning";
                sb.AppendLine("                        <tr>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\" title=\"{System.Net.WebUtility.HtmlEncode(m.LogPath)}\">{System.Net.WebUtility.HtmlEncode(m.LogScan)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\"><span class=\"monitoring-badge {cls}\">{System.Net.WebUtility.HtmlEncode(m.Pattern)}</span></td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{m.Count}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px; font-family:monospace; font-size:12px;\">{System.Net.WebUtility.HtmlEncode(m.SampleLine)}</td>");
                sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{RenderWikiLink(m.WikiUrl)}</td>");
                sb.AppendLine("                        </tr>");
            }
            sb.AppendLine("                    </tbody>");
            sb.AppendLine("                </table>");
        }

        return sb.ToString();
    }

    private static string RenderWikiLink(string? wikiUrl)
        => string.IsNullOrEmpty(wikiUrl)
            ? "—"
            : $"<a href=\"{System.Net.WebUtility.HtmlEncode(wikiUrl)}\" target=\"_blank\" rel=\"noopener\">Anleitung ↗</a>";

    private string GenerateCertificateSectionHtml(Dictionary<string, Minicon.SimpleAdmin.Models.State.CertificateState> certificates)
    {
        if (certificates.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("                <h3 style=\"margin-top:24px;\">🔒 Zertifikate</h3>");
        sb.AppendLine("                <table style=\"width:100%; border-collapse:collapse;\">");
        sb.AppendLine("                    <thead>");
        sb.AppendLine("                        <tr style=\"background:#f8f9fa; text-align:left;\">");
        sb.AppendLine("                            <th style=\"padding:6px 10px;\">Subject</th>");
        sb.AppendLine("                            <th style=\"padding:6px 10px;\">Thumbprint</th>");
        sb.AppendLine("                            <th style=\"padding:6px 10px;\">Läuft ab</th>");
        sb.AppendLine("                            <th style=\"padding:6px 10px;\">Restzeit</th>");
        sb.AppendLine("                            <th style=\"padding:6px 10px;\">Status</th>");
        sb.AppendLine("                        </tr>");
        sb.AppendLine("                    </thead>");
        sb.AppendLine("                    <tbody>");
        foreach (var (key, cert) in certificates.OrderBy(c => c.Value.DaysUntilExpiry))
        {
            var statusClass = cert.Status switch
            {
                Minicon.SimpleAdmin.Models.State.MetricStatus.Critical => "badge-critical",
                Minicon.SimpleAdmin.Models.State.MetricStatus.Warning => "badge-warning",
                Minicon.SimpleAdmin.Models.State.MetricStatus.Healthy => "badge-ok",
                _ => ""
            };
            var thumb = string.IsNullOrEmpty(cert.Thumbprint) ? "-" : (cert.Thumbprint.Length > 16 ? cert.Thumbprint[..16] + "…" : cert.Thumbprint);
            var expires = cert.ExpiresAt == default ? "-" : cert.ExpiresAt.ToString("yyyy-MM-dd");
            var rest = cert.DaysUntilExpiry <= 0 ? $"abgelaufen ({Math.Abs(cert.DaysUntilExpiry)} T)" : $"{cert.DaysUntilExpiry} Tage";
            sb.AppendLine("                        <tr>");
            sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{System.Net.WebUtility.HtmlEncode(cert.Subject)}</td>");
            sb.AppendLine($"                            <td style=\"padding:6px 10px; font-family:monospace; font-size:11px;\">{thumb}</td>");
            sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{expires}</td>");
            sb.AppendLine($"                            <td style=\"padding:6px 10px;\">{rest}</td>");
            sb.AppendLine($"                            <td style=\"padding:6px 10px;\"><span class=\"monitoring-badge {statusClass}\">{cert.Status}</span></td>");
            sb.AppendLine("                        </tr>");
        }
        sb.AppendLine("                    </tbody>");
        sb.AppendLine("                </table>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates HTML section for AppPool status from RuntimeStatus.AppPools
    /// </summary>
    private string GenerateAppPoolSectionHtml(Dictionary<string, AppPoolState> appPools)
    {
        if (appPools.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>IIS Application Pools</h2>");
        html.AppendLine("            <div class=\"metrics-grid\">");

        foreach (var pool in appPools.Values)
        {
            var poolStatusClass = pool.Status.ToString().ToLower();

            html.AppendLine($"                <div class=\"metric-card metric-{poolStatusClass}\">");
            html.AppendLine($"                    <h3>{WebUtility.HtmlEncode(pool.Name)}</h3>");
            html.AppendLine($"                    <p class=\"metric-value-large\">Status: <span class=\"status-badge status-{poolStatusClass}\">{pool.Status}</span></p>");
            if (pool.WorkerProcessId.HasValue)
            {
                var memoryClass = pool.MemoryStatus == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical ? "critical"
                               : pool.MemoryStatus == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning ? "warning" : "ok";
                html.AppendLine($"                    <p class=\"metric-status metric-{memoryClass}\">Memory: {pool.MemoryMB:F2} MB ({pool.MemoryStatus})</p>");
            }

            if (pool.WorkerProcessId.HasValue && pool.UptimeHours > 0)
            {
                var uptimeClass = pool.UptimeStatus == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning ? "warning" : "ok";
                html.AppendLine($"                    <p class=\"metric-status metric-{uptimeClass}\">Uptime: {pool.UptimeHours:F1} h ({pool.UptimeStatus})</p>");
            }

            if (pool.Status == Minicon.SimpleAdmin.Models.State.AppPoolStatus.Stopped && pool.StoppedSince.HasValue)
            {
                var stoppedDuration = DateTime.UtcNow - pool.StoppedSince.Value;
                html.AppendLine($"                    <p class=\"metric-status\">Stopped: {stoppedDuration:hh\\:mm\\:ss} ago</p>");
            }

            html.AppendLine($"                </div>");
        }

        html.AppendLine("            </div>");
        html.AppendLine("        </section>");

        return html.ToString();
    }

    /// <summary>
    /// Generates HTML section for Windows Service status from RuntimeStatus.Services
    /// </summary>
    private string GenerateWindowsServicesSectionHtml(Dictionary<string, ServiceState> services)
    {
        if (services.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>Windows Services</h2>");
        html.AppendLine("            <div class=\"metrics-grid\">");

        foreach (var (name, svc) in services.OrderBy(s => s.Key))
        {
            var statusClass = svc.IsHealthy ? "ok" : "critical";

            html.AppendLine($"                <div class=\"metric-card metric-{statusClass}\">");
            html.AppendLine($"                    <h3>{WebUtility.HtmlEncode(svc.DisplayName)}</h3>");
            html.AppendLine($"                    <p class=\"metric-status\"><code>{WebUtility.HtmlEncode(name)}</code></p>");
            var statusLabel = svc.Status == "PatternNoMatch" ? "Kein Dienst gefunden" : svc.Status;
            html.AppendLine($"                    <p class=\"metric-value-large\">Status: <span class=\"status-badge status-{statusClass}\">{WebUtility.HtmlEncode(statusLabel)}</span></p>");
            var startartLabel = svc.StartupType switch { "Automatic" => "Automatisch", "Manual" => "Manuell", _ => null };
            if (startartLabel != null)
                html.AppendLine($"                    <p class=\"metric-detail\">Startart: {WebUtility.HtmlEncode(startartLabel)}</p>");
            html.AppendLine($"                </div>");
        }

        html.AppendLine("            </div>");
        html.AppendLine("        </section>");

        return html.ToString();
    }

    /// <summary>
    /// Generates HTML section for Event Log results from RuntimeStatus.EventLogs
    /// </summary>
    private string GenerateEventLogSectionHtml(EventLogState eventLogs)
    {
        if (eventLogs.RecentEvents.Count == 0)
        {
            return string.Empty;
        }

        var statusClass = eventLogs.Status switch
        {
            Minicon.SimpleAdmin.Models.State.MetricStatus.Critical => "critical",
            Minicon.SimpleAdmin.Models.State.MetricStatus.Warning => "warning",
            _ => "ok"
        };

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine($"            <h2>Event Log <span class=\"status-badge status-{statusClass}\">{eventLogs.EventCount} Ereignisse</span></h2>");
        html.AppendLine("            <table class=\"event-log-table\">");
        html.AppendLine("                <thead><tr><th>Zeitstempel</th><th>Level</th><th>Quelle</th><th>ID</th><th>Meldung</th></tr></thead>");
        html.AppendLine("                <tbody>");

        foreach (var ev in eventLogs.RecentEvents)
        {
            var levelClass = ev.Level switch
            {
                "Critical" or "Error" => "critical",
                "Warning" => "warning",
                _ => "ok"
            };
            var message = ev.Message.Length > 120 ? ev.Message[..120] + "…" : ev.Message;
            html.AppendLine("                    <tr>");
            html.AppendLine($"                        <td>{ev.Timestamp:yyyy-MM-dd HH:mm:ss}</td>");
            html.AppendLine($"                        <td><span class=\"status-badge status-{levelClass}\">{WebUtility.HtmlEncode(ev.Level)}</span></td>");
            html.AppendLine($"                        <td>{WebUtility.HtmlEncode(ev.Source)}</td>");
            html.AppendLine($"                        <td>{ev.EventId}</td>");
            html.AppendLine($"                        <td>{WebUtility.HtmlEncode(message)}</td>");
            html.AppendLine("                    </tr>");
        }

        html.AppendLine("                </tbody>");
        html.AppendLine("            </table>");
        html.AppendLine("        </section>");

        return html.ToString();
    }

    private static string RenderSqlRowPreview(Minicon.SimpleAdmin.Models.State.SqlQueryResult result)
    {
        if (result.Rows.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<details style=\"margin-top:.5rem;\">");
        sb.Append($"<summary style=\"cursor:pointer;\">Ergebnis anzeigen ({result.Rows.Count} von {result.TotalRows} Zeilen)</summary>");
        sb.Append("<table class=\"sql-result-table\">");
        sb.Append("<thead><tr>");
        foreach (var col in result.Columns)
        {
            sb.Append($"<th>{WebUtility.HtmlEncode(col)}</th>");
        }
        sb.Append("</tr></thead><tbody>");
        foreach (var row in result.Rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
            {
                var display = cell is null ? "<em>NULL</em>" : WebUtility.HtmlEncode(cell);
                sb.Append($"<td>{display}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        if (result.TotalRows > result.Rows.Count)
        {
            sb.Append($"<p class=\"sql-result-note\">Nur die ersten {result.Rows.Count} von {result.TotalRows} Zeilen sind gespeichert.</p>");
        }
        sb.Append("</details>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates HTML section for SQL query check results
    /// </summary>
    private static string GenerateSqlQueryChecksSectionHtml(Dictionary<string, SqlQueryCheckState> checks)
    {
        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>SQL Query Checks</h2>");

        foreach (var (_, check) in checks)
        {
            var statusClass = check.Status switch
            {
                Minicon.SimpleAdmin.Models.State.MetricStatus.Critical => "critical",
                Minicon.SimpleAdmin.Models.State.MetricStatus.Warning => "warning",
                _ => "ok"
            };

            html.AppendLine($"            <div class=\"apppool-card pool-{(check.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Healthy ? "running" : "stopped")}\" style=\"margin-bottom:1rem;\">");
            html.AppendLine($"                <h3>{WebUtility.HtmlEncode(check.CheckName)} <span class=\"status-badge status-{statusClass}\">{check.Status}</span></h3>");

            if (!string.IsNullOrEmpty(check.ConnectionError))
            {
                html.AppendLine($"                <p style=\"color:#e74c3c;\"><strong>Verbindungsfehler:</strong> {WebUtility.HtmlEncode(check.ConnectionError)}</p>");
            }
            else if (check.Results.Count > 0)
            {
                html.AppendLine("                <table class=\"event-log-table\" style=\"margin-top:.5rem;\">");
                html.AppendLine("                    <thead><tr><th>Query</th><th>Ergebnis</th><th>Detail</th></tr></thead>");
                html.AppendLine("                    <tbody>");

                foreach (var result in check.Results)
                {
                    var qClass = result.Status switch
                    {
                        Minicon.SimpleAdmin.Models.State.MetricStatus.Critical => "critical",
                        Minicon.SimpleAdmin.Models.State.MetricStatus.Warning => "warning",
                        _ => "ok"
                    };

                    var detail = new StringBuilder();
                    if (!string.IsNullOrEmpty(result.ExecutionError))
                        detail.Append($"Fehler: {WebUtility.HtmlEncode(result.ExecutionError)}");

                    if (result.RowCount != null)
                    {
                        var rcText = result.RowCount.Passed
                            ? $"Zeilen: {result.RowCount.ActualCount} {result.RowCount.Operator} {result.RowCount.ExpectedCount} ✓"
                            : $"Zeilen: {result.RowCount.ActualCount} (erwartet {result.RowCount.Operator} {result.RowCount.ExpectedCount}) ✗";
                        if (detail.Length > 0) detail.Append(" | ");
                        detail.Append(rcText);
                    }

                    foreach (var col in result.ColumnResults)
                    {
                        var colText = col.Passed
                            ? $"{col.AssertionName}: {col.ActualValue} ✓"
                            : $"{col.AssertionName}: {col.ActualValue} (erwartet {col.Operator} '{col.ExpectedValue}') ✗";
                        if (detail.Length > 0) detail.Append(" | ");
                        detail.Append(colText);
                    }

                    html.AppendLine("                        <tr>");
                    html.AppendLine($"                            <td>{WebUtility.HtmlEncode(result.QueryName)}</td>");
                    html.AppendLine($"                            <td><span class=\"status-badge status-{qClass}\">{result.Status}</span></td>");
                    html.AppendLine($"                            <td>{detail}{RenderSqlRowPreview(result)}</td>");
                    html.AppendLine("                        </tr>");
                }

                html.AppendLine("                    </tbody>");
                html.AppendLine("                </table>");
            }

            html.AppendLine("            </div>");
        }

        html.AppendLine("        </section>");
        return html.ToString();
    }

    /// <summary>
    /// Generates HTML section for email probe states (sender role)
    /// </summary>
    private static string GenerateEmailProbesSectionHtml(Dictionary<string, EmailProbeState> probes)
    {
        if (probes.Count == 0)
            return string.Empty;

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>E-Mail-Probes (Sender)</h2>");
        html.AppendLine("            <table class=\"apppool-table\">");
        html.AppendLine("                <thead><tr><th>Name</th><th>Status</th><th>Letzte Sendung</th><th>Fehler</th></tr></thead>");
        html.AppendLine("                <tbody>");

        foreach (var (_, probe) in probes.OrderBy(kvp => kvp.Key))
        {
            var cls = probe.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical ? "critical"
                    : probe.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning   ? "warning"
                    : "ok";
            var lastSent = probe.LastSentAt.HasValue
                ? probe.LastSentAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                : "–";
            var error = WebUtility.HtmlEncode(probe.ErrorMessage ?? "–");

            html.AppendLine($"                    <tr>");
            html.AppendLine($"                        <td>{WebUtility.HtmlEncode(probe.ProbeName)}</td>");
            html.AppendLine($"                        <td><span class=\"status-badge status-{cls}\">{probe.Status}</span></td>");
            html.AppendLine($"                        <td>{lastSent}</td>");
            html.AppendLine($"                        <td style=\"color:{(probe.ErrorMessage != null ? "#e74c3c" : "inherit")}\">{error}</td>");
            html.AppendLine($"                    </tr>");
        }

        html.AppendLine("                </tbody>");
        html.AppendLine("            </table>");
        html.AppendLine("        </section>");
        return html.ToString();
    }

    /// <summary>
    /// Generates HTML section for email delivery check states (checker role)
    /// </summary>
    private static string GenerateEmailDeliverySectionHtml(Dictionary<string, EmailDeliveryCheckState> checks)
    {
        if (checks.Count == 0)
            return string.Empty;

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>E-Mail-Zustellung (Checker)</h2>");
        html.AppendLine("            <table class=\"apppool-table\">");
        html.AppendLine("                <thead><tr><th>Name</th><th>Status</th><th>Letzter Eingang</th><th>Alter (Min)</th><th>Fehler</th></tr></thead>");
        html.AppendLine("                <tbody>");

        foreach (var (_, check) in checks.OrderBy(kvp => kvp.Key))
        {
            var cls = check.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Critical ? "critical"
                    : check.Status == Minicon.SimpleAdmin.Models.State.MetricStatus.Warning   ? "warning"
                    : "ok";
            var lastReceived = check.LastReceivedAt.HasValue
                ? check.LastReceivedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                : "–";
            var age = check.AgeMinutes.HasValue ? $"{check.AgeMinutes.Value:F0}" : "–";
            var error = WebUtility.HtmlEncode(check.ErrorMessage ?? "–");

            html.AppendLine($"                    <tr>");
            html.AppendLine($"                        <td>{WebUtility.HtmlEncode(check.CheckName)}</td>");
            html.AppendLine($"                        <td><span class=\"status-badge status-{cls}\">{check.Status}</span></td>");
            html.AppendLine($"                        <td>{lastReceived}</td>");
            html.AppendLine($"                        <td>{age}</td>");
            html.AppendLine($"                        <td style=\"color:{(check.ErrorMessage != null ? "#e74c3c" : "inherit")}\">{error}</td>");
            html.AppendLine($"                    </tr>");
        }

        html.AppendLine("                </tbody>");
        html.AppendLine("            </table>");
        html.AppendLine("        </section>");
        return html.ToString();
    }

    /// <summary>
    /// Generates HTML section for active problems derived from metrics and AppPools
    /// </summary>
    private string GenerateProblemsSectionHtml(List<ActiveProblem> activeProblems)
    {
        if (activeProblems.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        html.AppendLine("        <section>");
        html.AppendLine("            <h2>Active Problems</h2>");
        html.AppendLine("            <div class=\"problems-list\">");

        foreach (var problem in activeProblems)
        {
            var severityClass = problem.Severity.ToString().ToLower();
            var ackStatus = problem.Acknowledged ? "Acknowledged" : "Unacknowledged";
            var problemTitle = WebUtility.HtmlEncode($"{problem.Type}: {problem.Source}");

            html.AppendLine($"                <div class=\"problem-card problem-{severityClass}\">");
            html.AppendLine($"                    <h3>{problemTitle}</h3>");
            html.AppendLine($"                    <p><strong>Severity:</strong> <span class=\"severity-badge severity-{severityClass}\">{problem.Severity}</span></p>");
            html.AppendLine($"                    <p><strong>Status:</strong> {ackStatus}</p>");
            html.AppendLine($"                    <p>{WebUtility.HtmlEncode(problem.Message)}</p>");

            if (problem.FirstOccurrence != default)
            {
                html.AppendLine($"                    <p><small>First: {problem.FirstOccurrence:yyyy-MM-dd HH:mm} UTC</small></p>");
            }

            if (problem.LastOccurrence != default)
            {
                html.AppendLine($"                    <p><small>Last: {problem.LastOccurrence:yyyy-MM-dd HH:mm} UTC</small></p>");
            }

            html.AppendLine($"                </div>");
        }

        html.AppendLine("            </div>");
        html.AppendLine("        </section>");

        return html.ToString();
    }

    /// <summary>
    /// Returns fallback documentation HTML
    /// </summary>
    private string GetFallbackDocumentationHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""de"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Minicon.SimpleAdmin - Konfigurationsdokumentation</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: #f5f5f5;
            color: #333;
            line-height: 1.6;
        }
        header {
            background: #2c3e50;
            color: white;
            padding: 2rem;
            text-align: center;
        }
        header h1 {
            font-size: 2rem;
            margin-bottom: 0.5rem;
        }
        nav {
            background: #34495e;
            padding: 1rem;
            position: sticky;
            top: 0;
            z-index: 100;
        }
        nav ul {
            list-style: none;
            display: flex;
            flex-wrap: wrap;
            justify-content: center;
            gap: 1rem;
            max-width: 1200px;
            margin: 0 auto;
        }
        nav a {
            color: white;
            text-decoration: none;
            padding: 0.5rem 1rem;
            border-radius: 4px;
            transition: background 0.3s;
        }
        nav a:hover {
            background: #2c3e50;
        }
        main {
            max-width: 1200px;
            margin: 2rem auto;
            padding: 0 1rem;
        }
        section {
            background: white;
            margin-bottom: 2rem;
            padding: 2rem;
            border-radius: 8px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.1);
        }
        h2 {
            color: #2c3e50;
            font-size: 1.8rem;
            margin-bottom: 1rem;
            padding-bottom: 0.5rem;
            border-bottom: 3px solid #3498db;
        }
        h3 {
            color: #34495e;
            font-size: 1.4rem;
            margin-top: 1.5rem;
            margin-bottom: 1rem;
        }
        h4 {
            color: #555;
            font-size: 1.1rem;
            margin-top: 1rem;
            margin-bottom: 0.5rem;
        }
        p {
            margin-bottom: 1rem;
        }
        ul, ol {
            margin-left: 2rem;
            margin-bottom: 1rem;
        }
        li {
            margin-bottom: 0.5rem;
        }
        code {
            background: #f8f9fa;
            padding: 0.2rem 0.4rem;
            border-radius: 3px;
            font-family: 'Consolas', 'Monaco', monospace;
            font-size: 0.9em;
        }
        pre {
            background: #2d2d2d;
            color: #f8f8f2;
            padding: 1.5rem;
            border-radius: 8px;
            overflow-x: auto;
            margin-bottom: 1rem;
            font-family: 'Consolas', 'Monaco', monospace;
            font-size: 0.9em;
            line-height: 1.5;
        }
        .info-box {
            background: #e3f2fd;
            border-left: 4px solid #2196f3;
            padding: 1rem;
            margin-bottom: 1rem;
        }
        .warning-box {
            background: #fff3e0;
            border-left: 4px solid #ff9800;
            padding: 1rem;
            margin-bottom: 1rem;
        }
        .success-box {
            background: #e8f5e9;
            border-left: 4px solid #4caf50;
            padding: 1rem;
            margin-bottom: 1rem;
        }
        table {
            width: 100%;
            border-collapse: collapse;
            margin-bottom: 1rem;
        }
        th, td {
            padding: 0.75rem;
            text-align: left;
            border-bottom: 1px solid #ddd;
        }
        th {
            background: #34495e;
            color: white;
            font-weight: bold;
        }
        tr:hover {
            background: #f5f5f5;
        }
        .back-link {
            display: inline-block;
            margin-top: 2rem;
            padding: 0.75rem 1.5rem;
            background: #3498db;
            color: white;
            text-decoration: none;
            border-radius: 4px;
            transition: background 0.3s;
        }
        .back-link:hover {
            background: #2980b9;
        }
    </style>
</head>
<body>
    <header>
        <h1>Minicon.SimpleAdmin - Konfigurationsdokumentation</h1>
        <p>Vollständige Anleitung zur Konfiguration und Verwendung</p>
    </header>

    <nav>
        <ul>
            <li><a href=""#overview"">Übersicht</a></li>
            <li><a href=""#structure"">Struktur</a></li>
            <li><a href=""#global"">Globale Einstellungen</a></li>
            <li><a href=""#environments"">Umgebungen</a></li>
            <li><a href=""#status-smoothing"">Status-Glättung</a></li>
            <li><a href=""#pages"">Generierte Seiten</a></li>
            <li><a href=""#prtg"">PRTG Monitoring</a></li>
            <li><a href=""#netscaler"">NetScaler Health Checks</a></li>
            <li><a href=""#ok-marker"">OK-Marker</a></li>
            <li><a href=""#examples"">Beispiele</a></li>
        </ul>
    </nav>

    <main>
        <section id=""overview"">
            <h2>Übersicht</h2>
            <p>
                Der Minicon.SimpleAdmin überwacht System-Metriken (CPU, Memory, Disk) und generiert statische HTML-Seiten
                für LoadBalancer (NetScaler) und Monitoring-Systeme (PRTG). Die Konfiguration erfolgt über die
                <code>config/config.json</code> Datei.
            </p>
            <div class=""info-box"">
                <strong>Datei-Speicherort:</strong> <code>Minicon.SimpleAdmin/config/config.json</code>
            </div>
        </section>

        <section id=""structure"">
            <h2>Grundstruktur der config.json</h2>
            <pre>{
  ""history"": {
    ""maxEntries"": 500,
    ""minConsecutiveChecks"": 5
  },
  ""output"": {
    ""okMarker"": ""##OK##""
  },
  ""environments"": {
    ""production"": { ""transfer"": [...], ""services"": [...], ""biztalk"": [...] },
    ""ref"": { ""transfer"": [...], ""services"": [...], ""biztalk"": [...] },
    ""development"": { ""transfer"": [...], ""services"": [...], ""biztalk"": [...] }
  },
  ""servers"": [
    {
      ""name"": ""server-name"",
      ""active"": true,
      ""baseUrl"": ""http://localhost:5067"",
      ""serviceTypes"": [...]
    }
  ]
}</pre>
        </section>

        <section id=""global"">
            <h2>Globale Einstellungen</h2>

            <h3>History (Verlaufsdaten)</h3>
            <table>
                <tr>
                    <th>Parameter</th>
                    <th>Typ</th>
                    <th>Beschreibung</th>
                    <th>Default</th>
                </tr>
                <tr>
                    <td><code>maxEntries</code></td>
                    <td>integer</td>
                    <td>Maximale Anzahl der Verlaufseinträge pro Service (FIFO-Rotation)</td>
                    <td>500</td>
                </tr>
                <tr>
                    <td><code>minConsecutiveChecks</code></td>
                    <td>integer</td>
                    <td>Anzahl aufeinanderfolgender Checks, die einen Status bestätigen müssen (Status-Glättung)</td>
                    <td>5</td>
                </tr>
            </table>

            <h3>Output (HTML-Ausgabe)</h3>
            <table>
                <tr>
                    <th>Parameter</th>
                    <th>Typ</th>
                    <th>Beschreibung</th>
                    <th>Default</th>
                </tr>
                <tr>
                    <td><code>okMarker</code></td>
                    <td>string</td>
                    <td>Text-Marker als HTML-Kommentar bei gesunden Services (für LoadBalancer/PRTG)</td>
                    <td>""##OK##""</td>
                </tr>
            </table>

            <div class=""success-box"">
                <strong>Beispiel:</strong>
                <pre>{
  ""history"": { ""maxEntries"": 1000, ""minConsecutiveChecks"": 5 },
  ""output"": { ""okMarker"": ""##OK##"" }
}</pre>
            </div>
        </section>

        <section id=""environments"">
            <h2>Umgebungen (Environments)</h2>
            <p>
                Server werden in Umgebungen gruppiert (z.B. Development, Ref, Production).
                Die <code>all.html</code> Seite zeigt eine Übersicht aller Umgebungen mit Statusabruf von allen erreichbaren Servern.
            </p>

            <h3>Konfiguration</h3>
            <pre>{
  ""environments"": {
    ""production"": {
      ""transfer"": [""prod-transfer-01"", ""prod-transfer-02""],
      ""services"": [""prod-services-01""],
      ""biztalk"": [""prod-biztalk-01""]
    },
    ""ref"": {
      ""transfer"": [""ref-transfer-01""],
      ""services"": [""ref-services-01""],
      ""biztalk"": [""ref-biztalk-01""]
    },
    ""development"": {
      ""transfer"": [""dev-transfer-01""],
      ""services"": [""dev-services-01""],
      ""biztalk"": [""dev-biztalk-01""]
    }
  }
}</pre>

            <div class=""info-box"">
                <strong>Hinweis:</strong> Umgebungen ohne erreichbare Server werden auf der <code>all.html</code> Seite automatisch ausgeblendet.
            </div>
        </section>

        <section id=""status-smoothing"">
            <h2>Status-Glättung (Alert-Fatigue-Prevention)</h2>
            <p>
                Um Fehlalarme durch temporäre CPU- oder Speicherspitzen zu vermeiden, müssen Statusänderungen durch
                <strong>N aufeinanderfolgende Checks</strong> bestätigt werden.
            </p>

            <h3>Funktionsweise</h3>
            <div class=""info-box"">
                <p><strong>Beispiel mit minConsecutiveChecks: 5</strong></p>
                <p>Verlauf:     CPU: [95%, 92%, 88%, 65%, 70%] (letzte 5 Messungen)</p>
                <p>Schwellwert: Warning: 70%, Critical: 85%</p>
                <p>Auswertung:  [Crit, Crit, Crit, Ok, Ok]</p>
                <p>→ Nur 3/5 aufeinanderfolgende Critical → Status wird <strong>nicht</strong> als Critical bestätigt</p>
                <p>→ Ergebnis: Status bleibt ""Ok"" (stabilster Status)</p>
            </div>

            <h3>Threshold-Operatoren</h3>
            <p>Schwellwerte unterstützen verschiedene Vergleichsoperatoren:</p>
            <table>
                <tr>
                    <th>Operator</th>
                    <th>Bedeutung</th>
                    <th>Beispiel</th>
                </tr>
                <tr>
                    <td><code>&gt;</code></td>
                    <td>Größer als (Standard)</td>
                    <td>CPU &gt; 85% → Critical</td>
                </tr>
                <tr>
                    <td><code>&gt;=</code></td>
                    <td>Größer oder gleich</td>
                    <td>Memory &gt;= 90% → Critical</td>
                </tr>
                <tr>
                    <td><code>&lt;</code></td>
                    <td>Kleiner als</td>
                    <td>Disk &lt; 10% frei → Critical</td>
                </tr>
                <tr>
                    <td><code>&lt;=</code></td>
                    <td>Kleiner oder gleich</td>
                    <td>Disk &lt;= 10% frei → Critical</td>
                </tr>
                <tr>
                    <td><code>==</code></td>
                    <td>Gleich</td>
                    <td>Wert == 0 → Critical</td>
                </tr>
                <tr>
                    <td><code>!=</code></td>
                    <td>Ungleich</td>
                    <td>Wert != 100 → Warning</td>
                </tr>
            </table>
        </section>

        <section id=""pages"">
            <h2>Generierte Seiten</h2>
            <p>SimpleAdmin generiert mehrere statische HTML-Seiten, die von IIS bereitgestellt werden:</p>

            <h3>index.html (Lokale Übersicht)</h3>
            <ul>
                <li>Zeigt alle Services des aktuellen Servers</li>
                <li>Auto-Refresh alle 60 Sekunden</li>
                <li>Link zur <code>all.html</code> Seite</li>
                <li>Link zur Dokumentation</li>
            </ul>

            <h3>all.html (Alle Umgebungen)</h3>
            <ul>
                <li>Zeigt <strong>alle Umgebungen</strong> (Production, Ref, Development)</li>
                <li>Ruft Status von allen erreichbaren Servern parallel ab</li>
                <li>Farbcodierte Umgebungsüberschriften (Production=rot, Ref=orange, Development=grün)</li>
                <li>Versteckt Umgebungen ohne erreichbare Server</li>
                <li>Auto-Refresh alle 60 Sekunden</li>
                <li>Zusammenfassungsleiste mit Healthy/Degraded/Unhealthy/Unknown Zählern</li>
            </ul>

            <h3>server.service.html (Detail-Seiten)</h3>
            <ul>
                <li>Detaillierte Metriken für jeden Service</li>
                <li>CPU, Memory, Disk-Werte mit Schwellwerten</li>
                <li>Verlaufszusammenfassung</li>
            </ul>

            <h3>admin/service/endpoint/index.html (PRTG/NetScaler)</h3>
            <ul>
                <li>Einfache Statusseiten für LoadBalancer und Monitoring</li>
                <li>Enthält OK-Marker als HTML-Kommentar wenn Service gesund</li>
            </ul>

            <div class=""warning-box"">
                <strong>Auto-Refresh:</strong> Alle Übersichtsseiten werden automatisch alle 60 Sekunden aktualisiert.
                Die statischen HTML-Seiten bleiben auch bei Ausfall des SimpleAdmin-Prozesses zugänglich.
            </div>
        </section>

        <section id=""ok-marker"">
            <h2>OK-Marker Konfiguration</h2>

            <h3>Was ist der OK-Marker?</h3>
            <p>
                Der OK-Marker ist ein spezieller Text (z.B. <code>##OK##</code>), der als <strong>HTML-Kommentar</strong>
                in die generierten HTML-Seiten eingefügt wird, wenn ein Service <strong>gesund</strong> ist.
            </p>

            <div class=""info-box"">
                <strong>Wichtig:</strong> Der Marker ist ein HTML-Kommentar und wird <strong>nicht auf der Webseite angezeigt</strong>,
                sondern nur im HTML-Quellcode!
            </div>

            <h3>Wofür wird er gebraucht?</h3>
            <ul>
                <li><strong>LoadBalancer</strong> (z.B. NetScaler) suchen im HTML-Quellcode nach diesem Text</li>
                <li><strong>PRTG</strong> kann im HTML-Quellcode nach diesem Text suchen</li>
                <li>Wenn der Text <strong>vorhanden</strong> ist → Service ist OK</li>
                <li>Wenn der Text <strong>fehlt</strong> → Service hat ein Problem</li>
            </ul>

            <h3>Konfiguration</h3>
            <pre>{
  ""output"": {
    ""okMarker"": ""##OK##""
  }
}</pre>

            <h3>LoadBalancer/PRTG Konfiguration</h3>

            <h4>NetScaler LoadBalancer</h4>
            <pre>Monitor Type: HTTP
URL: /admin/api-gateway/loadbalancer/
Response Code: 200
Response Contains: ##OK##</pre>

            <h4>PRTG Monitor</h4>
            <pre>Sensor Type: HTTP Advanced
URL: http://your-server.com/admin/api-gateway/monitor/
Expected HTTP Status: 200
Search String: ##OK##</pre>
        </section>

        <section id=""examples"">
            <h2>Vollständige Beispiele</h2>

            <h3>Beispiel 1: Multi-Umgebungs-Setup</h3>
            <pre>{
  ""history"": {
    ""maxEntries"": 500,
    ""minConsecutiveChecks"": 5
  },
  ""output"": { ""okMarker"": ""##OK##"" },
  ""environments"": {
    ""production"": {
      ""transfer"": [""prod-transfer-01""],
      ""services"": [""prod-services-01""],
      ""biztalk"": [""prod-biztalk-01""]
    },
    ""development"": {
      ""transfer"": [""localhost""],
      ""services"": [],
      ""biztalk"": []
    }
  },
  ""servers"": [
    {
      ""name"": ""localhost"",
      ""active"": true,
      ""baseUrl"": ""http://localhost:5067"",
      ""serviceTypes"": [
        {
          ""type"": ""transfer"",
          ""prtg"": {
            ""enabled"": true,
            ""adminPath"": ""/admin/transfer/monitor"",
            ""checks"": [
              {
                ""metric"": ""cpu"",
                ""displayName"": ""CPU Usage"",
                ""threshold"": { ""warning"": 70, ""critical"": 85, ""operator"": "">"" }
              },
              {
                ""metric"": ""memory"",
                ""displayName"": ""Memory Usage"",
                ""threshold"": { ""warning"": 80, ""critical"": 90, ""operator"": "">"" }
              },
              {
                ""metric"": ""disk"",
                ""displayName"": ""Disk Free Space"",
                ""target"": ""/"",
                ""threshold"": { ""warning"": 20, ""critical"": 10, ""operator"": ""<="" }
              }
            ]
          },
          ""netscaler"": {
            ""enabled"": true,
            ""adminPath"": ""/admin/transfer/loadbalancer"",
            ""healthChecks"": [
              {
                ""name"": ""basic-health"",
                ""method"": ""GET"",
                ""path"": ""/health"",
                ""expected"": { ""statusCodes"": [200] },
                ""intervalSeconds"": 30,
                ""timeoutMs"": 5000
              }
            ]
          }
        }
      ]
    }
  ]
}</pre>

            <h3>Metriken</h3>
            <table>
                <tr>
                    <th>Metrik</th>
                    <th>Beschreibung</th>
                    <th>Einheit</th>
                    <th>Typische Schwellwerte</th>
                </tr>
                <tr>
                    <td><code>cpu</code></td>
                    <td>CPU-Auslastung</td>
                    <td>%</td>
                    <td>Warning: 70%, Critical: 85% (Operator: &gt;)</td>
                </tr>
                <tr>
                    <td><code>memory</code></td>
                    <td>Speicherauslastung</td>
                    <td>%</td>
                    <td>Warning: 80%, Critical: 90% (Operator: &gt;)</td>
                </tr>
                <tr>
                    <td><code>disk</code></td>
                    <td>Freier Festplattenspeicher</td>
                    <td>%</td>
                    <td>Warning: 20%, Critical: 10% (Operator: &lt;=)</td>
                </tr>
            </table>

            <p class=""info-box"">
                Weitere detaillierte Beispiele und Konfigurationsoptionen finden Sie in der
                <a href=""https://github.com/minicon-zz/SimpleAdmin"">vollständigen Dokumentation</a>.
            </p>
        </section>

        <a href=""./"" class=""back-link"">← Zurück zur Übersicht</a>
    </main>

    <footer style=""background: #2c3e50; color: white; text-align: center; padding: 2rem; margin-top: 3rem;"">
        <p>Minicon.SimpleAdmin - © 2025</p>
    </footer>
</body>
</html>";
    }
}
