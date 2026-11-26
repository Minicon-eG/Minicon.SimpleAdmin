using System.Collections;
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
    private readonly ILogger<ConfigurationService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigurationService(IConfiguration configuration, ILogger<ConfigurationService>? logger = null)
    {
        _masterKey = configuration["Encryption:ConnectionStringKey"] ?? "";
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions RestoreJsonOptions = new(JsonOptions)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
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
    /// Gets the path of the loaded config.json, or null if the configuration was not loaded from a file
    /// </summary>
    public string? FilePath => _originalFilePath;

    internal bool HasConnectionStringKey => !string.IsNullOrEmpty(_masterKey);

    internal async Task<byte[]> ReadCurrentFileAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(_originalFilePath))
                throw new InvalidOperationException("No original file path set. Use LoadFromFile() first.");
            return await File.ReadAllBytesAsync(_originalFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task ReplaceFileAsync(byte[] contents)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(_originalFilePath))
                throw new InvalidOperationException("No original file path set. Use LoadFromFile() first.");
            var json = Backup.SettingsBackupArchive.DecodeUtf8(contents);
            ServiceConfigValidator.DeserializeAndValidate(json);
            JsonFileBackup.TryCreate(_originalFilePath, _logger);
            var tmp = _originalFilePath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tmp, contents);
                File.Move(tmp, _originalFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            LoadCore(json, RestoreJsonOptions);
        }
        catch
        {
            await ReloadCoreAsync(RestoreJsonOptions);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets whether the configuration has unsaved changes
    /// </summary>
    public bool HasChanges => _isLoaded && ExportToJson() != _originalJson;

    internal void LoadFromJson(string json) => LoadCore(json, JsonOptions);

    private void LoadCore(string json, JsonSerializerOptions readOptions)
    {
        var config = JsonSerializer.Deserialize<Config>(json, readOptions);
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

    internal async Task ReloadRestoredFileAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await ReloadCoreAsync(RestoreJsonOptions);
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

        JsonFileBackup.TryCreate(_originalFilePath, _logger);
        var tmp = _originalFilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, _originalFilePath, overwrite: true);
        MarkAsSaved();
    }

    private async Task ReloadCoreAsync(JsonSerializerOptions? readOptions = null)
    {
        if (string.IsNullOrEmpty(_originalFilePath)) return;
        var json = await File.ReadAllTextAsync(_originalFilePath);
        LoadCore(json, readOptions ?? JsonOptions);
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

    internal static void PreserveUnknownFields(ConfigModel replacement, ConfigModel? original)
    {
        if (original == null) return;

        foreach (var item in original.ExtensionData)
            replacement.ExtensionData.TryAdd(item.Key, item.Value.Clone());

        foreach (var property in original.GetType().GetProperties())
        {
            if (property.Name == nameof(ConfigModel.ExtensionData) || property.GetIndexParameters().Length > 0) continue;
            PreserveUnknownValues(property.GetValue(replacement), property.GetValue(original));
        }
    }

    private static void PreserveUnknownValues(object? replacement, object? original)
    {
        if (replacement is ConfigModel replacementModel && original is ConfigModel originalModel)
        {
            PreserveUnknownFields(replacementModel, originalModel);
            return;
        }

        if (replacement is IDictionary replacementDictionary && original is IDictionary originalDictionary)
        {
            foreach (var key in originalDictionary.Keys)
                if (replacementDictionary.Contains(key))
                    PreserveUnknownValues(replacementDictionary[key], originalDictionary[key]);
            return;
        }

        if (replacement is not IList replacementList || original is not IList originalList) return;

        var replacementKeys = replacementList.Cast<object?>().Select(StableListKey).ToList();
        var originalKeys = originalList.Cast<object?>().Select(StableListKey).ToList();
        for (var i = 0; i < replacementList.Count; i++)
        {
            var key = replacementKeys[i];
            var originalIndex = -1;
            if (key != null)
            {
                bool Matches(string? candidate) => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase);
                if (replacementKeys.Count(Matches) == 1 && originalKeys.Count(Matches) == 1)
                    originalIndex = originalKeys.FindIndex(Matches);
            }

            if (originalIndex < 0)
                originalIndex = FindUniqueRenameMatch(replacementList[i], replacementList, originalList);

            if (originalIndex >= 0)
            {
                PreserveUnknownValues(replacementList[i], originalList[originalIndex]);
                continue;
            }

            if (key == null && replacementList.Count == originalList.Count && originalKeys[i] == null)
                PreserveUnknownValues(replacementList[i], originalList[i]);
        }
    }

    private static int FindUniqueRenameMatch(object? replacement, IList replacementList, IList originalList)
    {
        var identity = FileMonitoringNameAndPath(replacement);
        if (identity == null) return -1;

        var type = replacement!.GetType();
        var (name, path) = identity.Value;
        var pathMatches = originalList.Cast<object?>()
            .Select((item, index) => (item, index))
            .Where(x => x.item?.GetType() == type
                        && string.Equals(FileMonitoringNameAndPath(x.item)?.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index)
            .ToList();
        if (pathMatches.Count == 1 && replacementList.Cast<object?>().Count(item =>
                item?.GetType() == type
                && string.Equals(FileMonitoringNameAndPath(item)?.Path, path, StringComparison.OrdinalIgnoreCase)) == 1)
            return pathMatches[0];

        var nameMatches = originalList.Cast<object?>()
            .Select((item, index) => (item, index))
            .Where(x => x.item?.GetType() == type
                        && string.Equals(FileMonitoringNameAndPath(x.item)?.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index)
            .ToList();
        return nameMatches.Count == 1 && replacementList.Cast<object?>().Count(item =>
                   item?.GetType() == type
                   && string.Equals(FileMonitoringNameAndPath(item)?.Name, name, StringComparison.OrdinalIgnoreCase)) == 1
            ? nameMatches[0]
            : -1;
    }

    private static (string Name, string Path)? FileMonitoringNameAndPath(object? value) => value switch
    {
        WatchedDirectory directory => (directory.Name, directory.Path),
        LogScanConfig logScan => (logScan.Name, logScan.Path),
        _ => null
    };

    private static string? StableListKey(object? value) => value switch
    {
        Server server when !string.IsNullOrWhiteSpace(server.Name) => nameof(Server) + ":" + server.Name,
        ServiceType serviceType when !string.IsNullOrWhiteSpace(serviceType.Type) => nameof(ServiceType) + ":" + serviceType.Type,
        Domain domain when !string.IsNullOrWhiteSpace(domain.Name) => nameof(Domain) + ":" + domain.Name,
        // Die überwachten Verzeichnisse und die Log-Scans haben keinen eindeutigen Namen
        // (der Kopier-Knopf hängt " (Kopie)" an). Der Schlüssel besteht aus Name und Pfad:
        // die Kopie hat einen anderen Namen, der Pfad bleibt gleich. Damit folgen die
        // unbekannten Felder dem Eintrag und nicht der Position in der Liste.
        // Mehrdeutige Schlüssel (zwei Einträge mit gleichem Name und Pfad) werden von
        // PreserveUnknownValues verworfen — es wird dann kein Feld geerbt. Bei einer reinen
        // Umbenennung findet FindUniqueRenameMatch den Eintrag über den unveränderten Pfad;
        // bei einer Pfadänderung entsprechend über den unveränderten Namen.
        WatchedDirectory directory when !string.IsNullOrWhiteSpace(directory.Name) =>
            nameof(WatchedDirectory) + ":" + directory.Name + "|" + directory.Path,
        LogScanConfig logScan when !string.IsNullOrWhiteSpace(logScan.Name) =>
            nameof(LogScanConfig) + ":" + logScan.Name + "|" + logScan.Path,
        _ => null
    };

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

        PreserveUnknownFields(server, _config.Servers[index]);
        _config.Servers[index] = server;

        // Update environment references if name changed
        if (!originalName.Equals(server.Name, StringComparison.OrdinalIgnoreCase))
        {
            UpdateServerNameInEnvironments(originalName, server.Name);
            UpdateServerNameInDomains(originalName, server.Name);
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
            RemoveServerFromDomains(name);
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

    #region Domain Operations

    /// <summary>
    /// Gets all domains, sorted by environment and name
    /// </summary>
    public IReadOnlyList<Domain> GetDomains() => (_config.Domains ??= new List<Domain>())
        .OrderBy(d => d.Environment, StringComparer.OrdinalIgnoreCase)
        .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .ToList()
        .AsReadOnly();

    /// <summary>
    /// Gets a domain by name (case-insensitive)
    /// </summary>
    public Domain? GetDomain(string name) =>
        (_config.Domains ??= new List<Domain>()).FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a new domain
    /// </summary>
    public void AddDomain(Domain domain)
    {
        if (GetDomain(domain.Name) != null)
        {
            throw new InvalidOperationException($"Domain '{domain.Name}' already exists");
        }

        _config.Domains.Add(domain);
    }

    /// <summary>
    /// Replaces an existing domain (the name may change)
    /// </summary>
    public void UpdateDomain(string originalName, Domain domain)
    {
        var domains = _config.Domains ??= new List<Domain>();
        var index = domains.FindIndex(d => d.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Domain '{originalName}' not found");
        }

        if (!originalName.Equals(domain.Name, StringComparison.OrdinalIgnoreCase) && GetDomain(domain.Name) != null)
        {
            throw new InvalidOperationException($"Domain '{domain.Name}' already exists");
        }

        PreserveUnknownFields(domain, domains[index]);
        domains[index] = domain;
    }

    /// <summary>
    /// Deletes a domain
    /// </summary>
    public void DeleteDomain(string name) =>
        (_config.Domains ??= new List<Domain>()).RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the names of the servers assigned to an environment (all categories), sorted
    /// </summary>
    public List<string> GetServerNamesInEnvironment(string environment) =>
        _config.GetServersInEnvironment(environment)
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Gets all distinct NetScaler health check names configured on any server
    /// </summary>
    public List<string> GetHealthCheckNames() =>
        _config.Servers
            .SelectMany(s => s.ServiceTypes)
            .SelectMany(st => st.NetScaler.HealthChecks)
            .Select(h => h.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Gets all distinct NetScaler vServer names used by domains
    /// </summary>
    public List<string> GetNetScalerVServerNames() =>
        (_config.Domains ?? new List<Domain>())
            .Select(d => d.NetScalerVServer)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateServerNameInDomains(string oldName, string newName)
    {
        foreach (var domain in _config.Domains ?? new List<Domain>())
        {
            ReplaceInList(domain.Servers, oldName, newName);
        }
    }

    private void RemoveServerFromDomains(string name)
    {
        foreach (var domain in _config.Domains ?? new List<Domain>())
        {
            domain.Servers.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
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
        PreserveUnknownFields(settings, _config.History);
        _config.History = settings;
    }

    /// <summary>
    /// Gets the output settings
    /// </summary>
    public OutputSettings GetOutputSettings() => _config.Output ?? new OutputSettings();

    /// <summary>Fallback when no title is configured in <c>general.title</c>.</summary>
    public const string DefaultApplicationTitle = "SimpleAdmin";

    /// <summary>Maximum title length — it has to fit into the navigation bar.</summary>
    public const int MaxApplicationTitleLength = 60;

    /// <summary>
    /// Title shown in the WebUI navigation and browser tab (<c>general.title</c>, fallback "SimpleAdmin").
    /// Safe to call before a config is loaded.
    /// </summary>
    public string GetApplicationTitle()
    {
        var title = _config.General?.Title?.Trim();
        return string.IsNullOrEmpty(title) ? DefaultApplicationTitle : title;
    }

    /// <summary>
    /// Sets the application title; empty or whitespace removes it (back to the default).
    /// Longer titles are cut to <see cref="MaxApplicationTitleLength"/> characters.
    /// </summary>
    public void UpdateApplicationTitle(string? title)
    {
        title = title?.Trim();
        if (title?.Length > MaxApplicationTitleLength) title = title[..MaxApplicationTitleLength].TrimEnd();

        _config.General ??= new GeneralSettings();
        _config.General.Title = string.IsNullOrEmpty(title) ? null : title;
    }

    /// <summary>
    /// Updates the output settings
    /// </summary>
    public void UpdateOutputSettings(OutputSettings settings)
    {
        PreserveUnknownFields(settings, _config.Output);
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
        PreserveUnknownFields(features, _config.Features);
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
        PreserveUnknownFields(timeProfiles, _config.TimeProfiles);
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
            PreserveUnknownFields(checks, server.Checks);
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
            PreserveUnknownFields(appPoolChecks, server.Checks.AppPools);
            server.Checks.AppPools = appPoolChecks;
        }
    }

    #endregion
}
