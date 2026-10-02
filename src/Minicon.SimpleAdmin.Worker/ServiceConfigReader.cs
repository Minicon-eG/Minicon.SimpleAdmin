using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Reads and validates config.json
/// </summary>
public class ServiceConfigReader : IServiceConfigReader
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<ServiceConfigReader> _logger;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the ServiceConfigReader class
    /// </summary>
    /// <param name="configPath">Path to the configuration file</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="fileSystem">File system abstraction</param>
    public ServiceConfigReader(string configPath, ILogger<ServiceConfigReader> logger, IFileSystem fileSystem)
    {
        _configPath = configPath;
        _logger = logger;
        _fileSystem = fileSystem;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    /// <summary>
    /// Loads and validates the configuration file
    /// </summary>
    public async Task<Config> LoadConfigAsync()
    {
        _logger.LogInformation("Loading configuration from: {ConfigPath}", _configPath);

        if (!_fileSystem.File.Exists(_configPath))
        {
            _logger.LogError("Configuration file not found: {ConfigPath}", _configPath);
            throw new FileNotFoundException($"Configuration file not found: {_configPath}");
        }

        try
        {
            var json = await _fileSystem.File.ReadAllTextAsync(_configPath);
            var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions);

            if (config == null)
            {
                _logger.LogError("Failed to deserialize configuration from {ConfigPath}", _configPath);
                throw new InvalidOperationException("Failed to deserialize configuration");
            }

            ValidateConfig(config);
            _logger.LogInformation("Configuration loaded successfully with {ServerCount} server(s)", config.Servers.Count);
            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in config file: {ConfigPath}", _configPath);
            throw new InvalidOperationException($"Invalid JSON in config file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates the configuration
    /// </summary>
    private void ValidateConfig(Config config)
    {
        _logger.LogDebug("Validating configuration");

        if (config.Servers == null || config.Servers.Count == 0)
        {
            _logger.LogError("Configuration validation failed: no servers defined");
            throw new InvalidOperationException("Configuration must contain at least one server");
        }

        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                _logger.LogError("Configuration validation failed: server name is empty");
                throw new InvalidOperationException("Server name cannot be empty");
            }

            if (server.ServiceTypes == null || server.ServiceTypes.Count == 0)
            {
                _logger.LogError("Configuration validation failed: server '{ServerName}' has no service types", server.Name);
                throw new InvalidOperationException($"Server '{server.Name}' must have at least one service type");
            }

            foreach (var serviceType in server.ServiceTypes)
            {
                if (string.IsNullOrWhiteSpace(serviceType.Type))
                {
                    _logger.LogError("Configuration validation failed: empty service type for server '{ServerName}'", server.Name);
                    throw new InvalidOperationException($"Service type cannot be empty for server '{server.Name}'");
                }
            }
        }

        // Validate history settings
        if (config.History != null)
        {
            if (config.History.MaxEntries < 0)
            {
                _logger.LogError("Configuration validation failed: MaxEntries is negative ({MaxEntries})", config.History.MaxEntries);
                throw new InvalidOperationException("MaxEntries cannot be negative");
            }

            if (config.History.MaxEntries > 10000)
            {
                _logger.LogError("Configuration validation failed: MaxEntries exceeds limit ({MaxEntries})", config.History.MaxEntries);
                throw new InvalidOperationException("MaxEntries cannot exceed 10000");
            }
        }

        _logger.LogDebug("Configuration validation successful");
    }

}
