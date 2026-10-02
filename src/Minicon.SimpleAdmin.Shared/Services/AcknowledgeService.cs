using System.Text.Json;
using System.Text.Json.Serialization;
using Minicon.SimpleAdmin.IO;
using Minicon.SimpleAdmin.Models.State;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Manages acknowledges with file persistence alongside the status directory
/// </summary>
public class AcknowledgeService : IAcknowledgeService
{
    private const string MetadataDirectoryName = "metadata";
    private readonly string _statusDirectory;
    private readonly string _acknowledgeMetadataDirectory;
    private readonly string _acknowledgesFilePath;
    private readonly ILogger<AcknowledgeService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private AcknowledgeState? _cached;
    private DateTime? _cachedFileLastWriteUtc;

    /// <summary>
    /// Initializes a new instance of the AcknowledgeService
    /// </summary>
    public AcknowledgeService(string statusDirectory, ILogger<AcknowledgeService> logger)
    {
        _statusDirectory = statusDirectory;
        _acknowledgeMetadataDirectory = Path.Combine(statusDirectory, MetadataDirectoryName);
        _acknowledgesFilePath = Path.Combine(_acknowledgeMetadataDirectory, "acknowledges.json");
        _logger = logger;

        Directory.CreateDirectory(statusDirectory);
        Directory.CreateDirectory(_acknowledgeMetadataDirectory);

        // Migrate from legacy locations if needed.
        MigrateLegacyAcknowledgeFile();
    }

    /// <inheritdoc />
    public async Task<AcknowledgeState> LoadAcknowledgesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cached != null)
            {
                if (!File.Exists(_acknowledgesFilePath) && _cachedFileLastWriteUtc == null)
                {
                    return _cached;
                }

                if (File.Exists(_acknowledgesFilePath))
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(_acknowledgesFilePath);
                    if (_cachedFileLastWriteUtc == lastWriteUtc)
                    {
                        return _cached;
                    }
                }
            }

            if (!File.Exists(_acknowledgesFilePath))
            {
                _logger.LogInformation("Acknowledges file not found, creating new state");
                _cached = new AcknowledgeState();
                _cachedFileLastWriteUtc = null;
                return _cached;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_acknowledgesFilePath);
                _cached = JsonSerializer.Deserialize<AcknowledgeState>(json, JsonOptions) ?? new AcknowledgeState();
                _cachedFileLastWriteUtc = File.GetLastWriteTimeUtc(_acknowledgesFilePath);
                _logger.LogDebug("Loaded {Count} active acknowledges", _cached.Acknowledges.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading acknowledges file, creating new state");
                _cached = new AcknowledgeState();
                _cachedFileLastWriteUtc = null;
            }

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAcknowledgesAsync(AcknowledgeState acknowledgeState)
    {
        await _lock.WaitAsync();
        try
        {
            acknowledgeState.LastUpdated = DateTime.UtcNow;
            _cached = acknowledgeState;

            var json = JsonSerializer.Serialize(acknowledgeState, JsonOptions);
            await File.WriteAllTextAsync(_acknowledgesFilePath, json);
            _cachedFileLastWriteUtc = File.GetLastWriteTimeUtc(_acknowledgesFilePath);
            _logger.LogDebug("Saved {Count} acknowledges", acknowledgeState.Acknowledges.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Acknowledge> CreateAcknowledgeAsync(
        string serverId,
        string problemId,
        string acknowledgedBy,
        string comment,
        int durationMinutes,
        bool autoResetOnHealthy = true,
        bool suppressAlerts = true,
        DateTime? expiresAt = null)
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();

        var acknowledge = Acknowledge.Create(
            serverId,
            problemId,
            acknowledgedBy,
            comment,
            durationMinutes,
            autoResetOnHealthy,
            suppressAlerts,
            expiresAt);

        acknowledgeState.Add(acknowledge);
        await SaveAcknowledgesAsync(acknowledgeState);

        _logger.LogInformation(
            "Created acknowledge {AckId} for problem {ProblemId} on server {ServerId} by {User}",
            acknowledge.Id, problemId, serverId, acknowledgedBy);

        return acknowledge;
    }

    /// <inheritdoc />
    public async Task ResolveAcknowledgeAsync(string acknowledgeId, string? resolution = null)
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();
        var acknowledge = acknowledgeState.GetById(acknowledgeId);

        if (acknowledge == null)
        {
            _logger.LogWarning("Acknowledge {AckId} not found", acknowledgeId);
            return;
        }

        acknowledge.Resolve(resolution);
        acknowledgeState.MoveToHistory(acknowledgeId);
        await SaveAcknowledgesAsync(acknowledgeState);

        _logger.LogInformation("Resolved acknowledge {AckId}", acknowledgeId);
    }

    /// <inheritdoc />
    public async Task CancelAcknowledgeAsync(string acknowledgeId)
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();
        var acknowledge = acknowledgeState.GetById(acknowledgeId);

        if (acknowledge == null)
        {
            _logger.LogWarning("Acknowledge {AckId} not found", acknowledgeId);
            return;
        }

        acknowledge.Cancel();
        acknowledgeState.MoveToHistory(acknowledgeId);
        await SaveAcknowledgesAsync(acknowledgeState);

        _logger.LogInformation("Cancelled acknowledge {AckId}", acknowledgeId);
    }

    /// <inheritdoc />
    public async Task ExtendAcknowledgeAsync(string acknowledgeId, int additionalMinutes)
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();
        var acknowledge = acknowledgeState.GetById(acknowledgeId);

        if (acknowledge == null)
        {
            _logger.LogWarning("Acknowledge {AckId} not found", acknowledgeId);
            return;
        }

        acknowledge.Extend(additionalMinutes);
        await SaveAcknowledgesAsync(acknowledgeState);

        _logger.LogInformation("Extended acknowledge {AckId} by {Minutes} minutes", acknowledgeId, additionalMinutes);
    }

    /// <inheritdoc />
    public async Task<List<string>> ProcessExpiredAcknowledgesAsync()
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();
        var expired = acknowledgeState.ProcessExpired();

        if (expired.Count > 0)
        {
            await SaveAcknowledgesAsync(acknowledgeState);
            _logger.LogInformation("Processed {Count} expired acknowledges", expired.Count);
        }

        return expired;
    }

    /// <inheritdoc />
    public async Task<Acknowledge?> GetAcknowledgeAsync(string acknowledgeId)
    {
        var acknowledgeState = await LoadAcknowledgesAsync();
        return acknowledgeState.GetById(acknowledgeId);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Acknowledge>> GetAcknowledgesForServerAsync(string serverId)
    {
        var acknowledgeState = await LoadAcknowledgesAsync();
        return acknowledgeState.GetByServerId(serverId);
    }

    /// <inheritdoc />
    public async Task<int> CleanupHistoryAsync(int retentionDays)
    {
        using var fileLock = await StateFilesLock.AcquireAsync(_statusDirectory);
        InvalidateCache();

        var acknowledgeState = await LoadAcknowledgesAsync();
        var removed = acknowledgeState.CleanupHistory(retentionDays);

        if (removed > 0)
        {
            await SaveAcknowledgesAsync(acknowledgeState);
            _logger.LogInformation("Cleaned up {Count} old acknowledge history entries", removed);
        }

        return removed;
    }

    /// <summary>
    /// Invalidates the cached state
    /// </summary>
    public void InvalidateCache()
    {
        _cached = null;
        _cachedFileLastWriteUtc = null;
    }

    /// <summary>
    /// Migrates acknowledges.json from legacy locations into the metadata directory if needed.
    /// </summary>
    private void MigrateLegacyAcknowledgeFile()
    {
        if (File.Exists(_acknowledgesFilePath))
        {
            return;
        }

        var parentDir = Path.GetDirectoryName(_statusDirectory);
        var legacyCandidates = new List<string>
        {
            Path.Combine(_statusDirectory, "acknowledges.json")
        };

        if (!string.IsNullOrEmpty(parentDir))
        {
            legacyCandidates.Add(Path.Combine(parentDir, "state", "acknowledges.json"));
        }

        var legacyPath = legacyCandidates.FirstOrDefault(File.Exists);
        if (legacyPath == null)
        {
            return;
        }

        try
        {
            File.Copy(legacyPath, _acknowledgesFilePath);
            _logger.LogInformation("Migrated acknowledges.json from {OldPath} to {NewPath}", legacyPath, _acknowledgesFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate acknowledges.json from {OldPath}", legacyPath);
        }
    }
}
