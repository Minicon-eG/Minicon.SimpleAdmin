using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Services.PlatformMetrics;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Fallback;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Linux;
using Minicon.SimpleAdmin.Services.PlatformMetrics.MacOS;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Windows;

namespace Minicon.SimpleAdmin.Tests.Services.PlatformMetrics;

/// <summary>
/// Integration tests for PlatformMetricsProviderFactory
/// </summary>
public class PlatformMetricsProviderFactoryTests
{
    private readonly Mock<ILogger> _mockLogger;

    public PlatformMetricsProviderFactoryTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    [Fact]
    public void Create_ReturnsCorrectProviderForCurrentPlatform()
    {
        // Act
        var provider = PlatformMetricsProviderFactory.Create(_mockLogger.Object);

        // Assert
        provider.Should().NotBeNull();
        provider.IsSupported().Should().BeTrue();

        if (OperatingSystem.IsWindows())
        {
            provider.Should().BeOfType<WindowsMetricsProvider>();
        }
        else if (OperatingSystem.IsLinux())
        {
            provider.Should().BeOfType<LinuxMetricsProvider>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            provider.Should().BeOfType<MacOsMetricsProvider>();
        }
        else
        {
            provider.Should().BeOfType<FallbackMetricsProvider>();
        }
    }

    [Fact]
    public void Create_ProviderIsSupported()
    {
        // Act
        var provider = PlatformMetricsProviderFactory.Create(_mockLogger.Object);

        // Assert
        provider.IsSupported().Should().BeTrue();
    }

    [Fact]
    public async Task Create_ProviderCanInitialize()
    {
        // Arrange
        var provider = PlatformMetricsProviderFactory.Create(_mockLogger.Object);

        // Act
        var act = async () => await provider.InitializeAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Create_ProviderCanCollectMetrics()
    {
        // Arrange
        var provider = PlatformMetricsProviderFactory.Create(_mockLogger.Object);
        await provider.InitializeAsync();

        // Act & Assert - CPU
        var cpuUsage = await provider.GetCpuUsageAsync();
        cpuUsage.Should().BeInRange(0, 100);

        // Act & Assert - Memory
        var memoryUsage = await provider.GetMemoryUsageAsync();
        memoryUsage.Should().BeInRange(0, 100);

        // Act & Assert - Disk
        var drive = OperatingSystem.IsWindows() ? "C:" : "/";
        var diskFreeSpace = await provider.GetDiskFreeSpaceAsync(drive);
        diskFreeSpace.Should().BeInRange(0, 100);
    }

    [Fact]
    public void Create_MultipleCalls_ReturnDifferentInstances()
    {
        // Act
        var provider1 = PlatformMetricsProviderFactory.Create(_mockLogger.Object);
        var provider2 = PlatformMetricsProviderFactory.Create(_mockLogger.Object);

        // Assert
        provider1.Should().NotBeSameAs(provider2);
    }
}
