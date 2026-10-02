using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks Windows Services status using sc.exe.
/// Falls back to empty results on non-Windows or when services are unavailable.
/// </summary>
public class WindowsServiceChecker : IWindowsServiceChecker
{
    private readonly ILogger<WindowsServiceChecker> _logger;
    private readonly bool _isWindows;

    public enum StartupType { Automatic, AutomaticDelayed, Manual, Disabled, Unknown }

    public WindowsServiceChecker(ILogger<WindowsServiceChecker> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Gets the status of all configured Windows Services
    /// </summary>
    public async Task<Dictionary<string, ServiceState>> CheckServicesAsync(
        List<ServiceCheckConfig> services,
        bool enabled)
    {
        var results = new Dictionary<string, ServiceState>();

        if (!_isWindows)
        {
            _logger.LogDebug("Not running on Windows, returning empty Windows Services results");
            return results;
        }

        if (!enabled || services.Count == 0)
        {
            return results;
        }

        return await CheckServicesInternalAsync(services);
    }

    /// <summary>
    /// Internal method to check multiple services, with wildcard expansion and startup-type filtering.
    /// </summary>
    private async Task<Dictionary<string, ServiceState>> CheckServicesInternalAsync(List<ServiceCheckConfig> services)
    {
        // Case-insensitive so the same service caught via a literal name AND a wildcard (or with
        // differing casing between config and sc.exe enumeration) collapses to a single entry.
        var results = new Dictionary<string, ServiceState>(StringComparer.OrdinalIgnoreCase);

        foreach (var serviceConfig in services)
        {
            try
            {
                if (serviceConfig.Name.Contains('*') || serviceConfig.Name.Contains('?'))
                {
                    await CheckWildcardServiceAsync(serviceConfig, results);
                }
                else
                {
                    var startupType = await GetStartupTypeAsync(serviceConfig.Name);

                    if (startupType == StartupType.Disabled)
                    {
                        _logger.LogDebug("Skipping disabled Windows Service {ServiceName}", serviceConfig.Name);
                        continue;
                    }

                    if (!ShouldMonitorService(startupType, serviceConfig))
                    {
                        _logger.LogDebug(
                            "Skipping Windows Service {ServiceName} (startup type {StartupType} not monitored per config)",
                            serviceConfig.Name, startupType);
                        continue;
                    }

                    var state = await CheckServiceAsync(serviceConfig.Name);
                    state.DisplayName = serviceConfig.GetEffectiveDisplayName();
                    state.StartupType = NormaliseStartupType(startupType);
                    results[serviceConfig.Name] = state;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Windows Service {ServiceName}", serviceConfig.Name);
                results[serviceConfig.Name] = new ServiceState
                {
                    Name = serviceConfig.Name,
                    DisplayName = serviceConfig.GetEffectiveDisplayName(),
                    Status = "Unknown",
                    IsHealthy = false
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Expands a wildcard pattern to matching services, filters by startup type, and checks each one.
    /// Adds a sentinel entry when no services match the pattern name at all.
    /// </summary>
    private async Task CheckWildcardServiceAsync(ServiceCheckConfig serviceConfig, Dictionary<string, ServiceState> results)
    {
        var allNames = await EnumerateAllServiceNamesAsync();
        var matches = allNames.Where(n => MatchesWildcard(serviceConfig.Name, n)).ToList();

        if (matches.Count == 0)
        {
            _logger.LogWarning("No Windows Services found for pattern '{Pattern}'", serviceConfig.Name);
            results[serviceConfig.Name] = new ServiceState
            {
                Name = serviceConfig.Name,
                DisplayName = serviceConfig.GetEffectiveDisplayName(),
                Status = "PatternNoMatch",
                IsHealthy = false
            };
            return;
        }

        foreach (var match in matches)
        {
            var startupType = await GetStartupTypeAsync(match);

            if (startupType == StartupType.Disabled)
            {
                _logger.LogDebug(
                    "Skipping disabled Windows Service {ServiceName} (matched by pattern '{Pattern}')",
                    match, serviceConfig.Name);
                continue;
            }

            if (!ShouldMonitorService(startupType, serviceConfig))
            {
                _logger.LogDebug(
                    "Skipping Windows Service {ServiceName} (startup type {StartupType} not monitored per config, pattern '{Pattern}')",
                    match, startupType, serviceConfig.Name);
                continue;
            }

            var state = await CheckServiceAsync(match);
            state.DisplayName = string.IsNullOrEmpty(serviceConfig.DisplayName)
                ? match
                : $"{serviceConfig.DisplayName} / {match}";
            state.StartupType = NormaliseStartupType(startupType);
            results[match] = state;
        }
    }

    /// <summary>
    /// Returns the startup type of a Windows service by parsing <c>sc.exe qc</c> output.
    /// Returns <see cref="StartupType.Unknown"/> on errors so that unknown services are still monitored.
    /// </summary>
    private async Task<StartupType> GetStartupTypeAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc \"{serviceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return StartupType.Unknown;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return ParseStartupType(output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking start type for service {ServiceName}", serviceName);
            return StartupType.Unknown;
        }
    }

    /// <summary>
    /// Parses the startup type from <c>sc.exe qc</c> output.
    /// </summary>
    public static StartupType ParseStartupType(string scOutput)
    {
        foreach (var line in scOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var valueStr = parts[1].Trim();
                    var typeCode = valueStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    return typeCode switch
                    {
                        "2" when valueStr.Contains("DELAYED", StringComparison.OrdinalIgnoreCase)
                            => StartupType.AutomaticDelayed,
                        "2" => StartupType.Automatic,
                        "3" => StartupType.Manual,
                        "4" => StartupType.Disabled,
                        _ => StartupType.Unknown
                    };
                }
            }
        }
        return StartupType.Unknown;
    }

    /// <summary>
    /// Returns whether a service with the given startup type should be monitored according to the check config.
    /// </summary>
    private static bool ShouldMonitorService(StartupType startupType, ServiceCheckConfig config)
    {
        return startupType switch
        {
            StartupType.Automatic or StartupType.AutomaticDelayed => config.MonitorAutomatic,
            StartupType.Manual => config.MonitorManual,
            StartupType.Disabled => false,
            _ => true  // Unknown: can't determine startup type, monitor anyway
        };
    }

    /// <summary>
    /// Maps the internal <see cref="StartupType"/> enum to the normalised string stored on <see cref="ServiceState"/>.
    /// Both <c>Automatic</c> and <c>AutomaticDelayed</c> collapse to <c>"Automatic"</c> to mirror the single
    /// <c>MonitorAutomatic</c> config flag.
    /// </summary>
    private static string? NormaliseStartupType(StartupType startupType) => startupType switch
    {
        StartupType.Automatic or StartupType.AutomaticDelayed => "Automatic",
        StartupType.Manual => "Manual",
        _ => null
    };

    /// <summary>
    /// Enumerates all Windows service names on the local machine via sc.exe.
    /// </summary>
    private async Task<List<string>> EnumerateAllServiceNamesAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query type= all state= all",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return [];

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var names = new List<string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = trimmed["SERVICE_NAME:".Length..].Trim();
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enumerating all Windows Services");
            return [];
        }
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> matches the wildcard <paramref name="pattern"/>.
    /// Supports <c>*</c> (any sequence) and <c>?</c> (single character). Case-insensitive.
    /// </summary>
    internal static bool MatchesWildcard(string pattern, string name)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Gets the status of a single Windows Service
    /// </summary>
    public async Task<ServiceState> CheckServiceAsync(string serviceName)
    {
        var state = new ServiceState
        {
            Name = serviceName,
            Status = "Unknown",
            IsHealthy = false
        };

        if (!_isWindows)
        {
            return state;
        }

        try
        {
            var serviceInfo = await QueryServiceAsync(serviceName);

            if (serviceInfo != null)
            {
                state.Status = serviceInfo;
                state.IsHealthy = state.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _logger.LogWarning("Windows Service {ServiceName} not found", serviceName);
                state.Status = "NotFound";
                state.IsHealthy = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Windows Service {ServiceName}", serviceName);
            state.Status = "Error";
            state.IsHealthy = false;
        }

        return state;
    }

    /// <summary>
    /// Queries a Windows Service using sc.exe
    /// </summary>
    private async Task<string?> QueryServiceAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query \"{serviceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse output: STATE : 4 RUNNING
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.TrimStart().StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var statePart = parts[1].Trim();
                        var stateNumber = statePart.Split(' ')[0];
                        return ParseServiceState(stateNumber);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error querying service {ServiceName}", serviceName);
            return null;
        }
    }

    /// <summary>
    /// Parses Windows Service state code to status string
    /// </summary>
    private static string ParseServiceState(string stateCode)
    {
        return stateCode switch
        {
            "1" => "Stopped",
            "2" => "Starting",
            "3" => "Stopping",
            "4" => "Running",
            "5" => "ContinuePending",
            "6" => "PausePending",
            "7" => "Paused",
            _ => "Unknown"
        };
    }
}
