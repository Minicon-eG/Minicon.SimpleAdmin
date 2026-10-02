using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Manages runtime status storage with history
/// </summary>
public class RuntimeStatusStore : IRuntimeStatusStore
{
    private readonly string _statusDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<RuntimeStatusStore> _logger;

    /// <summary>
    /// Initializes a new instance of the RuntimeStatusStore class
    /// </summary>
    /// <param name="statusDirectory">Directory path for status files</param>
    /// <param name="logger">Logger instance</param>
    public RuntimeStatusStore(string statusDirectory, ILogger<RuntimeStatusStore> logger)
    {
        _statusDirectory = statusDirectory;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // Ensure directory exists
        Directory.CreateDirectory(_statusDirectory);
        _logger.LogInformation("RuntimeStatusStore initialized with directory: {StatusDirectory}", _statusDirectory);
    }

    /// <summary>
    /// Saves or updates runtime status with history
    /// </summary>
    public async Task SaveStatusAsync(RuntimeStatus status, int maxHistoryEntries)
    {
        _logger.LogDebug("Saving status for {Server}.{Service}", status.Server, status.Service);

        var filePath = GetStatusFilePath(status.Server, status.Service);

        // Load existing status if available
        RuntimeStatus? existing = null;
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                existing = JsonSerializer.Deserialize<RuntimeStatus>(json, _jsonOptions);
                _logger.LogDebug("Loaded existing status for {Server}.{Service} with {HistoryCount} history entries",
                    status.Server, status.Service, existing?.History?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load existing status for {Server}.{Service}",
                    status.Server, status.Service);
            }
        }

        // Add current metrics to history
        if (status.Metrics.Count > 0)
        {
            var snapshot = new MetricSnapshot
            {
                Timestamp = status.LastUpdate,
                Values = status.Metrics.ToDictionary(
                    m => string.IsNullOrEmpty(m.Target) ? m.Name : $"{m.Name}_{m.Target}",
                    m => m.Value)
            };

            status.History.Add(snapshot);
        }

        // Merge with existing history
        if (existing?.History != null)
        {
            status.History.InsertRange(0, existing.History);
        }

        // Trim history to max entries (FIFO)
        if (status.History.Count > maxHistoryEntries)
        {
            var originalCount = status.History.Count;
            status.History = status.History
                .OrderByDescending(h => h.Timestamp)
                .Take(maxHistoryEntries)
                .OrderBy(h => h.Timestamp)
                .ToList();

            _logger.LogDebug("Trimmed history from {OriginalCount} to {NewCount} entries",
                originalCount, status.History.Count);
        }

        // Save to file
        var updatedJson = JsonSerializer.Serialize(status, _jsonOptions);
        await File.WriteAllTextAsync(filePath, updatedJson);

        _logger.LogInformation("Saved status for {Server}.{Service} with {HistoryCount} history entries",
            status.Server, status.Service, status.History.Count);
    }

    /// <summary>
    /// Loads runtime status from file
    /// </summary>
    public async Task<RuntimeStatus?> LoadStatusAsync(string server, string service)
    {
        _logger.LogDebug("Loading status for {Server}.{Service}", server, service);

        var filePath = GetStatusFilePath(server, service);

        if (!File.Exists(filePath))
        {
            _logger.LogDebug("Status file not found for {Server}.{Service}", server, service);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var status = JsonSerializer.Deserialize<RuntimeStatus>(json, _jsonOptions);
            _logger.LogInformation("Loaded status for {Server}.{Service}", server, service);
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading status for {Server}.{Service}", server, service);
            return null;
        }
    }

    /// <summary>
    /// Gets all status files
    /// </summary>
    public async Task<List<RuntimeStatus>> LoadAllStatusesAsync()
    {
        _logger.LogDebug("Loading all statuses from {StatusDirectory}", _statusDirectory);

        var statuses = new List<RuntimeStatus>();

        if (!Directory.Exists(_statusDirectory))
        {
            _logger.LogWarning("Status directory does not exist: {StatusDirectory}", _statusDirectory);
            return statuses;
        }

        var files = Directory.GetFiles(_statusDirectory, "*.json");
        _logger.LogDebug("Found {FileCount} status file(s)", files.Length);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var status = JsonSerializer.Deserialize<RuntimeStatus>(json, _jsonOptions);
                if (status != null &&
                    !string.IsNullOrWhiteSpace(status.Server) &&
                    !string.IsNullOrWhiteSpace(status.Service))
                {
                    statuses.Add(status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading status from file: {FilePath}", file);
            }
        }

        _logger.LogInformation("Loaded {StatusCount} status(es) from {FileCount} file(s)",
            statuses.Count, files.Length);

        return statuses;
    }

    /// <summary>
    /// Gets the file path for a specific server/service status
    /// </summary>
    private string GetStatusFilePath(string server, string service)
    {
        var fileName = $"{server}.{service}.json";
        return Path.Combine(_statusDirectory, fileName);
    }

    /// <summary>
    /// Deletes status file
    /// </summary>
    public void DeleteStatus(string server, string service)
    {
        var filePath = GetStatusFilePath(server, service);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted status file for {Server}.{Service}", server, service);
        }
        else
        {
            _logger.LogWarning("Cannot delete status file for {Server}.{Service} - file not found", server, service);
        }
    }
}
