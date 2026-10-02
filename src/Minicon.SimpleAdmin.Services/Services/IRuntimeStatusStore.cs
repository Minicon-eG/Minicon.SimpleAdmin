using Minicon.SimpleAdmin.Models.Status;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Interface for managing runtime status storage with history
/// </summary>
public interface IRuntimeStatusStore
{
    /// <summary>
    /// Saves or updates runtime status with history
    /// </summary>
    Task SaveStatusAsync(RuntimeStatus status, int maxHistoryEntries);

    /// <summary>
    /// Loads runtime status from file
    /// </summary>
    Task<RuntimeStatus?> LoadStatusAsync(string server, string service);

    /// <summary>
    /// Gets all status files
    /// </summary>
    Task<List<RuntimeStatus>> LoadAllStatusesAsync();

    /// <summary>
    /// Deletes status file
    /// </summary>
    void DeleteStatus(string server, string service);
}
