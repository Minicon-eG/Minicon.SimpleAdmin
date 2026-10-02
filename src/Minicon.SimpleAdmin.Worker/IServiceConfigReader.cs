using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Interface for reading and validating service configuration
/// </summary>
public interface IServiceConfigReader
{
    /// <summary>
    /// Loads and validates the configuration file
    /// </summary>
    Task<Config> LoadConfigAsync();
}
