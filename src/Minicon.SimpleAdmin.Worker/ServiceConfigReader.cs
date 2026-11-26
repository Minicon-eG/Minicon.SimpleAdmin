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
        ServiceConfigValidator.Validate(config);
        _logger.LogDebug("Configuration validation successful");
    }

}
