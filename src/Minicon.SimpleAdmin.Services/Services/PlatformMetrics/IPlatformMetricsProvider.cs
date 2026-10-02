namespace Minicon.SimpleAdmin.Services.PlatformMetrics;

/// <summary>
/// Platform-agnostic interface for collecting system metrics
/// </summary>
public interface IPlatformMetricsProvider : IDisposable
{
    /// <summary>
    /// Checks if this provider is supported on the current platform
    /// </summary>
    bool IsSupported();

    /// <summary>
    /// Initializes platform-specific resources
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Gets CPU usage percentage (0-100)
    /// </summary>
    Task<double> GetCpuUsageAsync();

    /// <summary>
    /// Gets memory usage percentage (0-100)
    /// </summary>
    Task<double> GetMemoryUsageAsync();

    /// <summary>
    /// Gets free disk space percentage for the specified drive
    /// </summary>
    /// <param name="drive">Drive identifier (e.g., "C:" on Windows, "/" on Unix)</param>
    Task<double> GetDiskFreeSpaceAsync(string drive);
}
