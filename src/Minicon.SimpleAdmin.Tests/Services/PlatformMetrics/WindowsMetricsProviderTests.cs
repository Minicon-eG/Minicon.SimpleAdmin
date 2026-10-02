using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Windows;

namespace Minicon.SimpleAdmin.Tests.Services.PlatformMetrics;

/// <summary>
/// Unit tests for WindowsMetricsProvider
/// </summary>
public class WindowsMetricsProviderTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly WindowsMetricsProvider _provider;

    public WindowsMetricsProviderTests()
    {
        _mockLogger = new Mock<ILogger>();
        _provider = new WindowsMetricsProvider(_mockLogger.Object);
    }

    [Fact]
    public void IsSupported_OnWindows_ReturnsTrue()
    {
        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task InitializeAsync_ShouldNotThrow()
    {
        // Act
        if (OperatingSystem.IsWindows())
        {
            var act = async () => await _provider.InitializeAsync();

            // Assert
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task GetCpuUsageAsync_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetCpuUsageAsync();

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetMemoryUsageAsync_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetMemoryUsageAsync();

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetDiskFreeSpaceAsync_WithValidDrive_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetDiskFreeSpaceAsync("C:");

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetDiskFreeSpaceAsync_WithInvalidDrive_ShouldReturnZero()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetDiskFreeSpaceAsync("Z:");

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Act
        var act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetCpuUsageAsync_CalledMultipleTimes_ShouldReturnConsistentValues()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on non-Windows platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result1 = await _provider.GetCpuUsageAsync();
        await Task.Delay(200); // Wait a bit for second measurement
        var result2 = await _provider.GetCpuUsageAsync();

        // Assert
        result1.Should().BeInRange(0, 100);
        result2.Should().BeInRange(0, 100);
        // Values should be somewhat close (within 50% of each other)
        Math.Abs(result1 - result2).Should().BeLessThan(50);
    }
}
