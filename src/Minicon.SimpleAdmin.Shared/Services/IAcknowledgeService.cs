using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Manages acknowledges for active problems with file persistence
/// </summary>
public interface IAcknowledgeService
{
    /// <summary>
    /// Loads all acknowledges from disk
    /// </summary>
    Task<AcknowledgeState> LoadAcknowledgesAsync();

    /// <summary>
    /// Creates a new acknowledge for an active problem
    /// </summary>
    Task<Acknowledge> CreateAcknowledgeAsync(
        string serverId,
        string problemId,
        string acknowledgedBy,
        string comment,
        int durationMinutes,
        bool autoResetOnHealthy = true,
        bool suppressAlerts = true,
        DateTime? expiresAt = null);

    /// <summary>
    /// Resolves an acknowledge (problem was fixed)
    /// </summary>
    Task ResolveAcknowledgeAsync(string acknowledgeId, string? resolution = null);

    /// <summary>
    /// Cancels an active acknowledge
    /// </summary>
    Task CancelAcknowledgeAsync(string acknowledgeId);

    /// <summary>
    /// Extends an acknowledge by additional minutes
    /// </summary>
    Task ExtendAcknowledgeAsync(string acknowledgeId, int additionalMinutes);

    /// <summary>
    /// Processes expired acknowledges and returns their IDs
    /// </summary>
    Task<List<string>> ProcessExpiredAcknowledgesAsync();

    /// <summary>
    /// Gets a specific acknowledge by ID
    /// </summary>
    Task<Acknowledge?> GetAcknowledgeAsync(string acknowledgeId);

    /// <summary>
    /// Gets all acknowledges for a specific server
    /// </summary>
    Task<IEnumerable<Acknowledge>> GetAcknowledgesForServerAsync(string serverId);

    /// <summary>
    /// Cleans up old acknowledge history entries
    /// </summary>
    Task<int> CleanupHistoryAsync(int retentionDays);

    /// <summary>
    /// Saves acknowledges to disk
    /// </summary>
    Task SaveAcknowledgesAsync(AcknowledgeState acknowledgeState);
}
