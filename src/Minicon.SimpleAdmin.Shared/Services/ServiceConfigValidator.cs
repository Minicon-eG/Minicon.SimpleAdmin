using System.Text.Json;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Services;

public static class ServiceConfigValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Config DeserializeAndValidate(string json)
    {
        Config? config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in config file: {ex.Message}", ex);
        }

        if (config == null) throw new InvalidOperationException("Failed to deserialize configuration");
        Validate(config);
        return config;
    }

    public static void Validate(Config config)
    {
        if (config.Servers == null || config.Servers.Count == 0)
            throw new InvalidOperationException("Configuration must contain at least one server");

        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException("Server name cannot be empty");
            if (server.ServiceTypes == null || server.ServiceTypes.Count == 0)
                throw new InvalidOperationException($"Server '{server.Name}' must have at least one service type");
            foreach (var serviceType in server.ServiceTypes)
                if (string.IsNullOrWhiteSpace(serviceType.Type))
                    throw new InvalidOperationException($"Service type cannot be empty for server '{server.Name}'");
        }

        if (config.History != null)
        {
            if (config.History.MaxEntries < 0)
                throw new InvalidOperationException("MaxEntries cannot be negative");
            if (config.History.MaxEntries > 10000)
                throw new InvalidOperationException("MaxEntries cannot exceed 10000");
        }
    }
}
