using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Services.PlatformMetrics.MacOS;

namespace Minicon.SimpleAdmin.Tests.Services.PlatformMetrics;

/// <summary>
/// Unit tests for MacOsMetricsProvider
/// </summary>
public class MacOsMetricsProviderTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly MacOsMetricsProvider _provider;

    public MacOsMetricsProviderTests()
    {
        _mockLogger = new Mock<ILogger>();
        _provider = new MacOsMetricsProvider(_mockLogger.Object);
    }

    [Fact]
    public void IsSupported_OnMacOS_ReturnsTrue()
    {
        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().Be(OperatingSystem.IsMacOS());
    }

    [Fact]
    public async Task InitializeAsync_OnMacOS_ShouldNotThrow()
    {
        // Act
        if (OperatingSystem.IsMacOS())
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
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
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
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetMemoryUsageAsync();

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetDiskFreeSpaceAsync_WithRootDrive_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetDiskFreeSpaceAsync("/");

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetDiskFreeSpaceAsync_WithInvalidDrive_ShouldReturnZero()
    {
        // Arrange
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetDiskFreeSpaceAsync("/nonexistent");

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
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result1 = await _provider.GetCpuUsageAsync();
        await Task.Delay(2000); // Wait a bit for the 'top' command to have different data
        var result2 = await _provider.GetCpuUsageAsync();

        // Assert
        result1.Should().BeInRange(0, 100);
        result2.Should().BeInRange(0, 100);
        // Values should be somewhat close (within 50% of each other typically)
        Math.Abs(result1 - result2).Should().BeLessThan(50);
    }

    [Fact]
    public async Task GetMemoryUsageAsync_CalledMultipleTimes_ShouldReturnConsistentValues()
    {
        // Arrange
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result1 = await _provider.GetMemoryUsageAsync();
        await Task.Delay(100);
        var result2 = await _provider.GetMemoryUsageAsync();

        // Assert
        result1.Should().BeInRange(0, 100);
        result2.Should().BeInRange(0, 100);
        // Memory should be fairly stable
        Math.Abs(result1 - result2).Should().BeLessThan(10);
    }
}
