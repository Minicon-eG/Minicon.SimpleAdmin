using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics.Fallback;

/// <summary>
/// Fallback metrics provider for unsupported platforms
/// Returns safe default values and logs warnings
/// </summary>
public class FallbackMetricsProvider : IPlatformMetricsProvider
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the FallbackMetricsProvider class
    /// </summary>
    public FallbackMetricsProvider(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if this provider is supported (always returns true as fallback)
    /// </summary>
    public bool IsSupported()
    {
        return true; // Fallback always "supports" the platform
    }

    /// <summary>
    /// Initializes the fallback provider
    /// </summary>
    public Task InitializeAsync()
    {
        _logger.LogWarning("Using fallback metrics provider - platform-specific metrics unavailable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets CPU usage percentage (returns 0 as fallback)
    /// </summary>
    public Task<double> GetCpuUsageAsync()
    {
        _logger.LogDebug("CPU metrics not available on this platform, returning 0");
        return Task.FromResult(0.0);
    }

    /// <summary>
    /// Gets memory usage percentage (returns 0 as fallback)
    /// </summary>
    public Task<double> GetMemoryUsageAsync()
    {
        _logger.LogDebug("Memory metrics not available on this platform, returning 0");
        return Task.FromResult(0.0);
    }

    /// <summary>
    /// Gets disk free space percentage (attempts to use DriveInfo as best effort)
    /// </summary>
    public async Task<double> GetDiskFreeSpaceAsync(string drive)
    {
        try
        {
            var driveInfo = new DriveInfo(drive);
            if (driveInfo.IsReady)
            {
                var freeSpacePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;
                _logger.LogDebug("Disk {Drive} free space (best effort): {Value}%", drive, freeSpacePercent);
                return Math.Max(0, Math.Min(100, freeSpacePercent));
            }

            _logger.LogWarning("Drive {Drive} is not ready", drive);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get disk info for {Drive} on unsupported platform", drive);
            return 0;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Disposes resources
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebug("Disposing fallback metrics provider");
        // No resources to dispose
    }
}
