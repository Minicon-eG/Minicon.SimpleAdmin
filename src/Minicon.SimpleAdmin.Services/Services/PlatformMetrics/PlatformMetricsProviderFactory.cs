using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Fallback;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Linux;
using Minicon.SimpleAdmin.Services.PlatformMetrics.MacOS;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Windows;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics;

/// <summary>
/// Factory for creating platform-specific metrics providers
/// </summary>
public static class PlatformMetricsProviderFactory
{
    /// <summary>
    /// Creates the appropriate metrics provider for the current platform
    /// </summary>
    public static IPlatformMetricsProvider Create(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            logger.LogInformation("Creating Windows metrics provider");
            return new WindowsMetricsProvider(logger);
        }

        if (OperatingSystem.IsLinux())
        {
            logger.LogInformation("Creating Linux metrics provider");
            return new LinuxMetricsProvider(logger);
        }

        if (OperatingSystem.IsMacOS())
        {
            logger.LogInformation("Creating macOS metrics provider");
            return new MacOsMetricsProvider(logger);
        }

        logger.LogWarning("Unsupported platform detected, using fallback provider");
        return new FallbackMetricsProvider(logger);
    }
}
