using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Services.PlatformMetrics.Linux;
using Minicon.SimpleAdmin.Services.PlatformMetrics.MacOS;

namespace Minicon.SimpleAdmin.Tests.Services.PlatformMetrics;

/// <summary>
/// Tests to ensure culture-invariant parsing across all platform providers
/// </summary>
public class CultureInvariantParsingTests
{
    private readonly Mock<ILogger> _mockLogger;

    public CultureInvariantParsingTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    [Fact]
    public async Task MacOsProvider_ParsesCpuUsageWithDot_InGermanCulture()
    {
        // Arrange
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            // Set German culture (uses comma as decimal separator)
            var germanCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = germanCulture;
            CultureInfo.CurrentUICulture = germanCulture;

            var provider = new MacOsMetricsProvider(_mockLogger.Object);
            await provider.InitializeAsync();

            // Act
            var cpuUsage = await provider.GetCpuUsageAsync();

            // Assert
            // CPU usage should be between 0-100, not multiplied by 100 (which would happen with culture-specific parsing)
            cpuUsage.Should().BeInRange(0, 100);
            cpuUsage.Should().NotBe(0); // Should have some CPU activity
        }
        finally
        {
            // Restore original culture
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public async Task LinuxProvider_ParsesMemoryInfo_InGermanCulture()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            // Set German culture
            var germanCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = germanCulture;
            CultureInfo.CurrentUICulture = germanCulture;

            var provider = new LinuxMetricsProvider(_mockLogger.Object);
            await provider.InitializeAsync();

            // Act
            var memoryUsage = await provider.GetMemoryUsageAsync();

            // Assert
            memoryUsage.Should().BeInRange(0, 100);
        }
        finally
        {
            // Restore original culture
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public async Task LinuxProvider_ParsesCpuStats_InGermanCulture()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            // Set German culture
            var germanCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = germanCulture;
            CultureInfo.CurrentUICulture = germanCulture;

            var provider = new LinuxMetricsProvider(_mockLogger.Object);
            await provider.InitializeAsync();

            // Act - First call establishes baseline
            await provider.GetCpuUsageAsync();
            await Task.Delay(200);
            var cpuUsage = await provider.GetCpuUsageAsync();

            // Assert
            cpuUsage.Should().BeInRange(0, 100);
        }
        finally
        {
            // Restore original culture
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Theory]
    [InlineData("de-DE")] // German - uses comma
    [InlineData("fr-FR")] // French - uses comma
    [InlineData("it-IT")] // Italian - uses comma
    [InlineData("es-ES")] // Spanish - uses comma
    [InlineData("en-US")] // English US - uses dot
    [InlineData("en-GB")] // English UK - uses dot
    public async Task MacOsProvider_WorksWithVariousCultures(string cultureName)
    {
        // Arrange
        if (!OperatingSystem.IsMacOS())
        {
            return; // Skip on non-macOS platforms
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            var testCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = testCulture;
            CultureInfo.CurrentUICulture = testCulture;

            var provider = new MacOsMetricsProvider(_mockLogger.Object);
            await provider.InitializeAsync();

            // Act
            var cpuUsage = await provider.GetCpuUsageAsync();
            var memoryUsage = await provider.GetMemoryUsageAsync();

            // Assert
            cpuUsage.Should().BeInRange(0, 100, $"CPU usage should be valid for culture {cultureName}");
            memoryUsage.Should().BeInRange(0, 100, $"Memory usage should be valid for culture {cultureName}");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("it-IT")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public async Task LinuxProvider_WorksWithVariousCultures(string cultureName)
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            return; // Skip on non-Linux platforms
        }

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            var testCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = testCulture;
            CultureInfo.CurrentUICulture = testCulture;

            var provider = new LinuxMetricsProvider(_mockLogger.Object);
            await provider.InitializeAsync();

            // Act
            var memoryUsage = await provider.GetMemoryUsageAsync();
            await provider.GetCpuUsageAsync(); // Baseline
            await Task.Delay(200);
            var cpuUsage = await provider.GetCpuUsageAsync();

            // Assert
            cpuUsage.Should().BeInRange(0, 100, $"CPU usage should be valid for culture {cultureName}");
            memoryUsage.Should().BeInRange(0, 100, $"Memory usage should be valid for culture {cultureName}");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }

    [Fact]
    public void DoubleParseWithInvariantCulture_ParsesCorrectly()
    {
        // Arrange
        var testValue = "42.90";
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            // Set German culture (uses comma as decimal separator)
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Act - Parse with InvariantCulture (correct way)
            var resultInvariant = double.Parse(testValue, CultureInfo.InvariantCulture);

            // Assert - With InvariantCulture, parsing is correct
            resultInvariant.Should().BeApproximately(42.9, 0.01);
            resultInvariant.Should().NotBe(4290); // Correct: not 4290

            // Demonstrate the problem WITHOUT InvariantCulture
            var parseSuccess = double.TryParse(testValue, out var resultWithoutInvariant);

            // In German culture, "42.90" with a dot is interpreted as "42" with thousands separator "." and "90" ignored
            // OR as thousands: 42 * 1000 + 90 = 42090 (depending on the parser)
            // Actually on some systems it becomes 4290 (treating dot as thousands separator incorrectly)
            if (parseSuccess)
            {
                // WRONG: Without InvariantCulture, the value is incorrectly parsed in German culture
                // This demonstrates WHY we need InvariantCulture
                resultWithoutInvariant.Should().NotBe(resultInvariant,
                    "because without InvariantCulture, parsing uses current culture and produces wrong result");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void LongParseWithInvariantCulture_ParsesCorrectly()
    {
        // Arrange
        var testValue = "1234567";
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            // Set German culture
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Act
            var result = long.Parse(testValue, CultureInfo.InvariantCulture);

            // Assert
            result.Should().Be(1234567);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
