namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Root configuration object
/// </summary>
public class Config
{
    /// <summary>
    /// Gets or sets the general settings
    /// </summary>
    public GeneralSettings? General { get; set; }

    /// <summary>
    /// Gets or sets the global feature toggles
    /// </summary>
    public FeaturesConfig? Features { get; set; }

    /// <summary>
    /// Gets or sets the time profiles for threshold adjustments
    /// </summary>
    public TimeProfilesConfig? TimeProfiles { get; set; }

    /// <summary>
    /// Gets or sets the environment definitions (development, ref, production)
    /// </summary>
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new();

    /// <summary>
    /// Gets or sets the history settings for metric data retention
    /// </summary>
    public HistorySettings? History { get; set; }

    /// <summary>
    /// Gets or sets the output settings for generated HTML pages
    /// </summary>
    public OutputSettings? Output { get; set; }

    /// <summary>
    /// Gets or sets the list of servers to monitor
    /// </summary>
    public List<Server> Servers { get; set; } = new();

    /// <summary>
    /// Gets the effective features configuration (with defaults if not set)
    /// </summary>
    public FeaturesConfig GetEffectiveFeatures() => Features ?? new FeaturesConfig();

    /// <summary>
    /// Gets the effective time profiles configuration (with defaults if not set)
    /// </summary>
    public TimeProfilesConfig GetEffectiveTimeProfiles() => TimeProfiles ?? new TimeProfilesConfig();

    /// <summary>
    /// Checks if a specific feature is globally enabled
    /// </summary>
    /// <param name="featureName">Feature name (appPools, eventLog, certificates, acknowledge)</param>
    /// <returns>True if the feature is enabled globally</returns>
    public bool IsFeatureEnabled(string featureName)
    {
        var features = GetEffectiveFeatures();
        return featureName.ToLowerInvariant() switch
        {
            "systemetrics" => features.SystemMetrics.Enabled,
            "services" => features.Services.Enabled,
            "apppools" => features.AppPools.Enabled,
            "eventlog" => features.EventLog.Enabled,
            "certificates" => features.Certificates.Enabled,
            "acknowledge" => features.Acknowledge.Enabled,
            "sqlqueries" => features.SqlQueries.Enabled,
            _ => false
        };
    }

    /// <summary>
    /// Finds which environment a server belongs to
    /// </summary>
    /// <param name="serverName">The server name to look up</param>
    /// <returns>The environment name (e.g., "development", "ref", "production") or null if not found</returns>
    public string? GetEnvironmentForServer(string serverName)
    {
        foreach (var env in Environments)
        {
            if (env.Value.GetAllServerNames().Any(s => s.Equals(serverName, StringComparison.OrdinalIgnoreCase)))
            {
                return env.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets all servers that belong to the specified environment
    /// </summary>
    /// <param name="environmentName">The environment name</param>
    /// <returns>List of server configurations in that environment</returns>
    public List<Server> GetServersInEnvironment(string environmentName)
    {
        if (!Environments.TryGetValue(environmentName, out var envConfig))
        {
            return new List<Server>();
        }

        var serverNames = envConfig.GetAllServerNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Servers.Where(s => serverNames.Contains(s.Name)).ToList();
    }
}

