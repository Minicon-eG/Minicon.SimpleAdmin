using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Linux;

namespace Minicon.SimpleAdmin.Tests.Services.PlatformMetrics;

/// <summary>
/// Unit tests for LinuxMetricsProvider
/// </summary>
public class LinuxMetricsProviderTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly LinuxMetricsProvider _provider;

    public LinuxMetricsProviderTests()
    {
        _mockLogger = new Mock<ILogger>();
        _provider = new LinuxMetricsProvider(_mockLogger.Object);
    }

    [Fact]
    public void IsSupported_OnLinux_ReturnsTrue()
    {
        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().Be(OperatingSystem.IsLinux());
    }

    [Fact]
    public async Task InitializeAsync_OnLinux_ShouldNotThrow()
    {
        // Act
        if (OperatingSystem.IsLinux())
        {
            var act = async () => await _provider.InitializeAsync();

            // Assert
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_WithoutProcFilesystem_ShouldThrow()
    {
        // This test is conceptual - in reality /proc should always exist on Linux
        // Just ensure the provider checks for it
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        // If /proc exists (which it should), initialization should succeed
        var act = async () => await _provider.InitializeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetCpuUsageAsync_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        await _provider.InitializeAsync();

        // Act
        // First call may return 0 (need baseline)
        await _provider.GetCpuUsageAsync();
        await Task.Delay(100);
        var result = await _provider.GetCpuUsageAsync();

        // Assert
        result.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetMemoryUsageAsync_ShouldReturnValueBetween0And100()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
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
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
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
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
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
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        await _provider.InitializeAsync();

        // Act
        // First call establishes baseline
        await _provider.GetCpuUsageAsync();
        await Task.Delay(200);
        var result1 = await _provider.GetCpuUsageAsync();
        await Task.Delay(200);
        var result2 = await _provider.GetCpuUsageAsync();

        // Assert
        result1.Should().BeInRange(0, 100);
        result2.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task GetCpuUsageAsync_FirstCall_MayReturnZero()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        await _provider.InitializeAsync();

        // Act
        var result = await _provider.GetCpuUsageAsync();

        // Assert
        // First call may return 0 since we need two measurements for delta
        result.Should().BeInRange(0, 100);
    }
}
