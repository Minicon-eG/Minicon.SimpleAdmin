using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks IIS Application Pool status using Microsoft.Web.Administration
/// Falls back to empty results on non-Windows or when IIS is not available
/// </summary>
public class AppPoolChecker : IAppPoolChecker
{
    private readonly ILogger<AppPoolChecker> _logger;
    private readonly bool _isWindows;
    private readonly bool _isIisAvailable;

    public AppPoolChecker(ILogger<AppPoolChecker> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _isIisAvailable = _isWindows && CheckIisAvailability();
    }

    /// <summary>
    /// Checks if IIS is available on this machine
    /// </summary>
    public bool IsIisAvailable => _isIisAvailable;

    /// <summary>
    /// Gets the status of all configured AppPools for a server
    /// </summary>
    public async Task<Dictionary<string, AppPoolState>> CheckAppPoolsAsync(
        AppPoolChecksConfig config,
        AppPoolDefaults defaults,
        Dictionary<string, AppPoolState>? previousStates = null)
    {
        var results = new Dictionary<string, AppPoolState>();

        if (!_isIisAvailable)
        {
            _logger.LogDebug("IIS not available, returning empty AppPool results");
            return results;
        }

        if (!config.Enabled)
        {
            return results;
        }

        // Determine which pools to check
        List<string> poolsToCheck;

        if (config.DiscoverAll)
        {
            // Discover all pools and filter by ignore list
            var allPools = await DiscoverAppPoolsAsync();
            poolsToCheck = allPools
                .Where(p => !IsPoolIgnored(p, config.IgnoredPools))
                .ToList();
            
            _logger.LogDebug("Discovered {Total} pools, checking {Count} (ignored: {Ignored})", 
                allPools.Count, poolsToCheck.Count, config.IgnoredPools.Count);
        }
        else
        {
            // Use explicitly configured pools, expanding wildcard patterns via discovery
            var enabledPools = config.Pools.Where(p => p.Enabled).ToList();
            var hasWildcards = enabledPools.Any(p => p.Name.Contains('*'));

            List<string>? allDiscovered = hasWildcards ? await DiscoverAppPoolsAsync() : null;

            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in enabledPools)
            {
                if (pool.Name.Contains('*') && allDiscovered != null)
                {
                    var matches = allDiscovered.Where(n => MatchesWildcard(n, pool.Name)).ToList();
                    if (matches.Count == 0)
                        _logger.LogWarning("Wildcard pattern '{Pattern}' matched no AppPools", pool.Name);
                    else
                        _logger.LogDebug("Wildcard pattern '{Pattern}' matched {Count} pool(s)", pool.Name, matches.Count);
                    foreach (var m in matches)
                        expanded.Add(m);
                }
                else
                {
                    expanded.Add(pool.Name);
                }
            }

            poolsToCheck = expanded.ToList();
        }

        foreach (var poolName in poolsToCheck)
        {
            try
            {
                // Check if there's specific config for this pool
                var poolConfig = config.Pools.FirstOrDefault(p =>
                    p.Name.Equals(poolName, StringComparison.OrdinalIgnoreCase));

                var state = await CheckAppPoolAsync(poolName, poolConfig, defaults);

                // Carry forward StoppedSince from the previous run so it is not reset on every check
                if (state.Status == AppPoolStatus.Stopped &&
                    previousStates != null &&
                    previousStates.TryGetValue(poolName, out var prev) &&
                    prev.Status == AppPoolStatus.Stopped &&
                    prev.StoppedSince.HasValue)
                {
                    state.StoppedSince = prev.StoppedSince;
                }

                results[poolName] = state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking AppPool {PoolName}", poolName);
                results[poolName] = new AppPoolState
                {
                    Name = poolName,
                    Status = AppPoolStatus.Unknown
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Discovers all AppPool names on this IIS server
    /// </summary>
    public async Task<List<string>> DiscoverAppPoolsAsync()
    {
        var pools = new List<string>();

        if (!_isIisAvailable)
        {
            return pools;
        }

        try
        {
            var output = await RunAppcmdAsync("list apppool /text:APPPOOL.NAME");
            if (!string.IsNullOrEmpty(output))
            {
                pools = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }

            _logger.LogDebug("Discovered {Count} AppPools: {Pools}", pools.Count, string.Join(", ", pools));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering AppPools");
        }

        return pools;
    }

    /// <summary>
    /// Checks if a pool name matches any pattern in the ignore list
    /// Supports wildcards: "Legacy*", "*Test*", "Dev_*"
    /// </summary>
    private static bool IsPoolIgnored(string poolName, List<string> ignoredPools)
    {
        if (ignoredPools.Count == 0)
            return false;

        foreach (var pattern in ignoredPools)
        {
            if (MatchesWildcard(poolName, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Simple wildcard matching (* only)
    /// </summary>
    private static bool MatchesWildcard(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        // Exact match
        if (!pattern.Contains('*'))
            return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        // Convert wildcard pattern to simple checks
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
        {
            var middle = pattern.Trim('*');
            return text.Contains(middle, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith("*"))
        {
            var suffix = pattern.TrimStart('*');
            return text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern.TrimEnd('*');
            return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Fallback: exact match
        return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the status of a single AppPool
    /// </summary>
    public async Task<AppPoolState> CheckAppPoolAsync(
        string poolName,
        AppPoolConfig? poolConfig = null,
        AppPoolDefaults? defaults = null)
    {
        defaults ??= new AppPoolDefaults();
        var state = new AppPoolState { Name = poolName };

        if (!_isIisAvailable)
        {
            state.Status = AppPoolStatus.Unknown;
            return state;
        }

        try
        {
            // Use appcmd.exe for IIS interaction (more reliable than Microsoft.Web.Administration in some scenarios)
            var poolInfo = await GetAppPoolInfoAsync(poolName);

            if (poolInfo == null)
            {
                _logger.LogWarning("AppPool {PoolName} not found", poolName);
                state.Status = AppPoolStatus.Unknown;
                return state;
            }

            state.Status = poolInfo.Value.Status;
            state.WorkerProcessId = poolInfo.Value.WorkerProcessId;

            if (state.Status == AppPoolStatus.Running && state.WorkerProcessId.HasValue)
            {
                // Get memory usage from the worker process
                try
                {
                    var process = Process.GetProcessById(state.WorkerProcessId.Value);
                    state.MemoryMB = process.WorkingSet64 / (1024.0 * 1024.0);

                    // Calculate uptime from process start time
                    var uptime = DateTime.Now - process.StartTime;
                    state.UptimeHours = uptime.TotalHours;
                    state.LastRecycle = process.StartTime;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not get process info for PID {Pid}", state.WorkerProcessId);
                }
            }
            else if (state.Status == AppPoolStatus.Stopped)
            {
                state.StoppedSince = DateTime.UtcNow;
                state.StopReason = "AppPool is stopped";
            }

            // Evaluate memory status based on thresholds
            var memWarning = poolConfig?.MemoryWarningMB ?? defaults.MemoryWarningMB;
            var memCritical = poolConfig?.MemoryCriticalMB ?? defaults.MemoryCriticalMB;

            state.MemoryStatus = state.MemoryMB switch
            {
                var m when m >= memCritical => MetricStatus.Critical,
                var m when m >= memWarning => MetricStatus.Warning,
                _ => MetricStatus.Healthy
            };
            state.MemoryWarningMB = memWarning;
            state.MemoryCriticalMB = memCritical;

            var uptimeWarning = poolConfig?.UptimeWarningHours ?? defaults.UptimeWarningHours;
            state.UptimeWarningHours = uptimeWarning;
            state.UptimeStatus = state.Status == AppPoolStatus.Running && uptimeWarning > 0 && state.UptimeHours >= uptimeWarning
                ? MetricStatus.Warning
                : MetricStatus.Healthy;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking AppPool {PoolName}", poolName);
            state.Status = AppPoolStatus.Unknown;
        }

        return state;
    }

    /// <summary>
    /// Gets AppPool info using appcmd.exe
    /// </summary>
    private async Task<(AppPoolStatus Status, int? WorkerProcessId)?> GetAppPoolInfoAsync(string poolName)
    {
        try
        {
            // Get AppPool state
            var stateOutput = await RunAppcmdAsync($"list apppool \"{poolName}\" /text:state");
            if (string.IsNullOrEmpty(stateOutput))
            {
                return null;
            }

            var status = ParseAppPoolStatus(stateOutput.Trim());

            // Get worker process ID if running
            int? workerPid = null;
            if (status == AppPoolStatus.Running)
            {
                var wpOutput = await RunAppcmdAsync($"list wp /apppool.name:\"{poolName}\" /text:WP.NAME");
                // Take only the first line: web gardens return one PID per line
                var firstLine = wpOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(firstLine) && int.TryParse(firstLine, out var pid))
                {
                    workerPid = pid;
                }
            }

            return (status, workerPid);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting AppPool info for {PoolName}", poolName);
            return null;
        }
    }

    /// <summary>
    /// Runs appcmd.exe with the given arguments
    /// </summary>
    private async Task<string> RunAppcmdAsync(string arguments)
    {
        var appcmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "inetsrv",
            "appcmd.exe");

        if (!File.Exists(appcmdPath))
        {
            _logger.LogDebug("appcmd.exe not found at {Path}", appcmdPath);
            return string.Empty;
        }

        var psi = new ProcessStartInfo
        {
            FileName = appcmdPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return string.Empty;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output;
    }

    /// <summary>
    /// Parses AppPool status string to enum
    /// </summary>
    private static AppPoolStatus ParseAppPoolStatus(string statusString)
    {
        return statusString.ToLowerInvariant() switch
        {
            "started" => AppPoolStatus.Running,
            "starting" => AppPoolStatus.Starting,
            "stopped" => AppPoolStatus.Stopped,
            "stopping" => AppPoolStatus.Stopping,
            _ => AppPoolStatus.Unknown
        };
    }

    /// <summary>
    /// Checks if IIS is available by looking for appcmd.exe
    /// </summary>
    private bool CheckIisAvailability()
    {
        var appcmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "inetsrv",
            "appcmd.exe");

        var exists = File.Exists(appcmdPath);
        _logger.LogDebug("IIS availability check: appcmd.exe at {Path} exists={Exists}", appcmdPath, exists);

        return exists;
    }
}
