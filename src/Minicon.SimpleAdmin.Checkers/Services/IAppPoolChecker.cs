using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Interface for IIS Application Pool checking
/// </summary>
public interface IAppPoolChecker
{
    /// <summary>
    /// Gets the status of all configured AppPools for a server
    /// </summary>
    /// <param name="config">AppPool checks configuration</param>
    /// <param name="defaults">Global default thresholds</param>
    /// <param name="previousStates">Previous AppPool states (used to carry forward StoppedSince)</param>
    /// <returns>Dictionary of pool name to AppPool state</returns>
    Task<Dictionary<string, AppPoolState>> CheckAppPoolsAsync(
        AppPoolChecksConfig config,
        AppPoolDefaults defaults,
        Dictionary<string, AppPoolState>? previousStates = null);

    /// <summary>
    /// Gets the status of a single AppPool
    /// </summary>
    /// <param name="poolName">The AppPool name</param>
    /// <param name="poolConfig">Optional pool-specific configuration</param>
    /// <param name="defaults">Global default thresholds</param>
    /// <returns>The AppPool state</returns>
    Task<AppPoolState> CheckAppPoolAsync(
        string poolName,
        AppPoolConfig? poolConfig = null,
        AppPoolDefaults? defaults = null);

    /// <summary>
    /// Checks if IIS is available on this machine
    /// </summary>
    bool IsIisAvailable { get; }

    /// <summary>
    /// Discovers all AppPool names on this IIS server
    /// </summary>
    /// <returns>List of all AppPool names</returns>
    Task<List<string>> DiscoverAppPoolsAsync();
}
