using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.WebUI.Services;

/// <summary>
/// Service for managing the SimpleAdmin configuration in memory
/// </summary>
public class ConfigurationService
{
    private Config _config = new();
    private string? _originalJson;
    private string? _originalFilePath;
    private bool _isLoaded;
    private readonly string _masterKey;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigurationService(IConfiguration configuration)
    {
        _masterKey = configuration["Encryption:ConnectionStringKey"] ?? "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Gets the current configuration
    /// </summary>
    public Config Config => _config;

    /// <summary>
    /// Gets whether a configuration has been loaded
    /// </summary>
    public bool IsLoaded => _isLoaded;

    /// <summary>
    /// Gets the error message from the last failed load attempt, or null if no error occurred
    /// </summary>
    public string? LoadError { get; internal set; }

    /// <summary>
    /// Gets whether the configuration has unsaved changes
    /// </summary>
    public bool HasChanges => _isLoaded && ExportToJson() != _originalJson;

    internal void LoadFromJson(string json)
    {
        var config = JsonSerializer.Deserialize<Config>(json, JsonOptions);
        if (config == null)
        {
            throw new InvalidOperationException("Failed to parse configuration JSON");
        }

        _config = config;
        // Re-serialize for fair comparison (input format may differ from our output format)
        _originalJson = JsonSerializer.Serialize(_config, JsonOptions);
        _isLoaded = true;
    }

    /// <summary>
    /// Loads a configuration from JSON string and remembers the file path for saving
    /// </summary>
    internal void LoadFromFile(string json, string filePath)
    {
        LoadFromJson(json);
        _originalFilePath = filePath;
    }

    /// <summary>
    /// Decrypts a connection string value. Returns the value unchanged if not encrypted or key is unconfigured.
    /// </summary>
    public string DecryptConnectionString(string value)
    {
        if (string.IsNullOrEmpty(_masterKey) || string.IsNullOrEmpty(value))
            return value;
        return ConnectionStringEncryption.Decrypt(value, _masterKey);
    }

    /// <summary>
    /// Encrypts all plain-text SQL connection strings on a server object in place.
    /// No-op if encryption key is not configured.
    /// </summary>
    public void EncryptServerConnectionStrings(Server server)
    {
        if (string.IsNullOrEmpty(_masterKey)) return;

        // SQL connection strings
        var sqlChecks = server.Checks?.SqlQueries?.Checks;
        if (sqlChecks != null)
        {
            foreach (var check in sqlChecks)
            {
                if (!string.IsNullOrEmpty(check.ConnectionString)
                    && !ConnectionStringEncryption.IsEncrypted(check.ConnectionString))
                {
                    check.ConnectionString = ConnectionStringEncryption.Encrypt(check.ConnectionString, _masterKey);
                }
            }
        }

        // Email probe SMTP passwords
        var emailProbes = server.Checks?.EmailProbes?.Probes;
        if (emailProbes != null)
        {
            foreach (var probe in emailProbes)
            {
                if (!string.IsNullOrEmpty(probe.Smtp.Password)
                    && !ConnectionStringEncryption.IsEncrypted(probe.Smtp.Password))
                {
                    probe.Smtp.Password = ConnectionStringEncryption.Encrypt(probe.Smtp.Password, _masterKey);
                }
            }
        }

        // Email delivery IMAP password
        var imapPassword = server.Checks?.EmailDelivery?.Imap.Password;
        if (!string.IsNullOrEmpty(imapPassword)
            && !ConnectionStringEncryption.IsEncrypted(imapPassword)
            && server.Checks?.EmailDelivery != null)
        {
            server.Checks.EmailDelivery.Imap.Password = ConnectionStringEncryption.Encrypt(imapPassword, _masterKey);
        }
    }

    /// <summary>
    /// Applies a mutation and saves atomically under the write lock.
    /// If the save fails, reverts in-memory state to disk before re-throwing,
    /// so concurrent requests always see a consistent snapshot.
    /// </summary>
    public async Task MutateAndSaveAsync(Action mutate)
    {
        await _writeLock.WaitAsync();
        try
        {
            mutate();
            await SaveCoreAsync();
        }
        catch
        {
            await ReloadCoreAsync();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Saves the current configuration back to the original file path under the write lock.
    /// Prefer <see cref="MutateAndSaveAsync"/> for mutation + save in one atomic step.
    /// </summary>
    public async Task SaveToFileAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reloads the configuration from disk under the write lock, discarding in-memory changes.
    /// </summary>
    public async Task ReloadFromFileAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await ReloadCoreAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SaveCoreAsync()
    {
        if (string.IsNullOrEmpty(_originalFilePath))
            throw new InvalidOperationException("No original file path set. Use LoadFromFile() first.");

        var json = ExportToJson();

        if (!string.IsNullOrEmpty(_masterKey))
        {
            var configCopy = JsonSerializer.Deserialize<Config>(json, JsonOptions)!;
            foreach (var server in configCopy.Servers)
                EncryptServerConnectionStrings(server);
            json = JsonSerializer.Serialize(configCopy, JsonOptions);
        }

        var tmp = _originalFilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, _originalFilePath, overwrite: true);
        MarkAsSaved();
    }

    private async Task ReloadCoreAsync()
    {
        if (string.IsNullOrEmpty(_originalFilePath)) return;
        var json = await File.ReadAllTextAsync(_originalFilePath);
        LoadFromJson(json);
    }

    private void EncryptConnectionStrings()
    {
        foreach (var server in _config.Servers)
        {
            EncryptServerConnectionStrings(server);
        }
    }

    internal string ExportToJson()
    {
        return JsonSerializer.Serialize(_config, JsonOptions);
    }

    internal void CreateNew()
    {
        _config = new Config
        {
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["development"] = new EnvironmentConfig(),
                ["ref"] = new EnvironmentConfig(),
                ["production"] = new EnvironmentConfig()
            },
            History = new HistorySettings
            {
                MaxEntries = 500,
                MinConsecutiveChecks = 5
            },
            Output = new OutputSettings
            {
                OkMarker = "##OK##"
            },
            Servers = new List<Server>()
        };
        _originalJson = ExportToJson();
        _isLoaded = true;
    }

    /// <summary>
    /// Marks the current state as saved (updates the original JSON)
    /// </summary>
    public void MarkAsSaved()
    {
        _originalJson = ExportToJson();
    }

    #region Server Operations

    /// <summary>
    /// Gets all servers
    /// </summary>
    public IReadOnlyList<Server> GetServers() => _config.Servers.AsReadOnly();

    /// <summary>
    /// Gets a server by name
    /// </summary>
    public Server? GetServer(string name) =>
        _config.Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a new server
    /// </summary>
    public void AddServer(Server server)
    {
        if (_config.Servers.Any(s => s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Server '{server.Name}' already exists");
        }
        _config.Servers.Add(server);
    }

    /// <summary>
    /// Updates an existing server
    /// </summary>
    public void UpdateServer(string originalName, Server server)
    {
        var index = _config.Servers.FindIndex(s => s.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Server '{originalName}' not found");
        }

        // If name changed, check for duplicates
        if (!originalName.Equals(server.Name, StringComparison.OrdinalIgnoreCase) &&
            _config.Servers.Any(s => s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Server '{server.Name}' already exists");
        }

        _config.Servers[index] = server;

        // Update environment references if name changed
        if (!originalName.Equals(server.Name, StringComparison.OrdinalIgnoreCase))
        {
            UpdateServerNameInEnvironments(originalName, server.Name);
        }
    }

    /// <summary>
    /// Deletes a server
    /// </summary>
    public void DeleteServer(string name)
    {
        var server = _config.Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (server != null)
        {
            _config.Servers.Remove(server);
            RemoveServerFromEnvironments(name);
        }
    }

    #endregion

    #region Environment Operations

    /// <summary>
    /// Gets all environment names
    /// </summary>
    public IReadOnlyList<string> GetEnvironmentNames() => _config.Environments.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Gets servers for a specific environment and category
    /// </summary>
    public List<string> GetEnvironmentServers(string environment, string category)
    {
        if (!_config.Environments.TryGetValue(environment, out var envConfig))
        {
            return new List<string>();
        }

        return category.ToLower() switch
        {
            "transfer" => envConfig.Transfer,
            "services" => envConfig.Services,
            "biztalk" => envConfig.Biztalk,
            "database" => envConfig.Database,
            _ => new List<string>()
        };
    }

    /// <summary>
    /// Sets servers for a specific environment and category
    /// </summary>
    public void SetEnvironmentServers(string environment, string category, List<string> servers)
    {
        if (!_config.Environments.TryGetValue(environment, out var envConfig))
        {
            envConfig = new EnvironmentConfig();
            _config.Environments[environment] = envConfig;
        }

        switch (category.ToLower())
        {
            case "transfer":
                envConfig.Transfer = servers;
                break;
            case "services":
                envConfig.Services = servers;
                break;
            case "biztalk":
                envConfig.Biztalk = servers;
                break;
            case "database":
                envConfig.Database = servers;
                break;
        }
    }

    private void UpdateServerNameInEnvironments(string oldName, string newName)
    {
        foreach (var env in _config.Environments.Values)
        {
            ReplaceInList(env.Transfer, oldName, newName);
            ReplaceInList(env.Services, oldName, newName);
            ReplaceInList(env.Biztalk, oldName, newName);
            ReplaceInList(env.Database, oldName, newName);
        }
    }

    private void RemoveServerFromEnvironments(string name)
    {
        foreach (var env in _config.Environments.Values)
        {
            env.Transfer.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            env.Services.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            env.Biztalk.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            env.Database.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void ReplaceInList(List<string> list, string oldValue, string newValue)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(oldValue, StringComparison.OrdinalIgnoreCase))
            {
                list[i] = newValue;
            }
        }
    }

    #endregion

    #region Settings Operations

    /// <summary>
    /// Gets the history settings
    /// </summary>
    public HistorySettings GetHistorySettings() => _config.History ?? new HistorySettings();

    /// <summary>
    /// Updates the history settings
    /// </summary>
    public void UpdateHistorySettings(HistorySettings settings)
    {
        _config.History = settings;
    }

    /// <summary>
    /// Gets the output settings
    /// </summary>
    public OutputSettings GetOutputSettings() => _config.Output ?? new OutputSettings();

    /// <summary>
    /// Updates the output settings
    /// </summary>
    public void UpdateOutputSettings(OutputSettings settings)
    {
        _config.Output = settings;
    }

    #endregion

    #region Features Operations

    /// <summary>
    /// Gets the global features configuration
    /// </summary>
    public FeaturesConfig GetFeatures() => _config.Features ?? new FeaturesConfig();

    /// <summary>
    /// Updates the global features configuration
    /// </summary>
    public void UpdateFeatures(FeaturesConfig features)
    {
        _config.Features = features;
    }

    #endregion

    #region TimeProfiles Operations

    /// <summary>
    /// Gets the time profiles configuration
    /// </summary>
    public TimeProfilesConfig GetTimeProfiles() => _config.TimeProfiles ?? new TimeProfilesConfig();

    /// <summary>
    /// Updates the time profiles configuration
    /// </summary>
    public void UpdateTimeProfiles(TimeProfilesConfig timeProfiles)
    {
        _config.TimeProfiles = timeProfiles;
    }

    #endregion

    #region Server Checks Operations

    /// <summary>
    /// Gets the server checks configuration
    /// </summary>
    public ServerChecksConfig? GetServerChecks(string serverName)
    {
        var server = GetServer(serverName);
        return server?.Checks;
    }

    /// <summary>
    /// Updates the server checks configuration
    /// </summary>
    public void UpdateServerChecks(string serverName, ServerChecksConfig checks)
    {
        var server = GetServer(serverName);
        if (server != null)
        {
            server.Checks = checks;
        }
    }

    /// <summary>
    /// Gets the AppPool checks configuration for a server
    /// </summary>
    public AppPoolChecksConfig? GetAppPoolChecks(string serverName)
    {
        var server = GetServer(serverName);
        return server?.Checks?.AppPools;
    }

    /// <summary>
    /// Updates the AppPool checks configuration for a server
    /// </summary>
    public void UpdateAppPoolChecks(string serverName, AppPoolChecksConfig appPoolChecks)
    {
        var server = GetServer(serverName);
        if (server != null)
        {
            server.Checks ??= new ServerChecksConfig();
            server.Checks.AppPools = appPoolChecks;
        }
    }

    #endregion
}
