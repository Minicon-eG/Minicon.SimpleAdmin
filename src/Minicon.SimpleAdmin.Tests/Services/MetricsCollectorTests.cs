using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Services.PlatformMetrics;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Unit tests for MetricsCollector
/// </summary>
public class MetricsCollectorTests
{
    private readonly Mock<IPlatformMetricsProvider> _mockPlatformProvider;
    private readonly Mock<ILogger<MetricsCollector>> _mockLogger;
    private readonly MetricsCollector _collector;

    public MetricsCollectorTests()
    {
        _mockPlatformProvider = new Mock<IPlatformMetricsProvider>();
        _mockLogger = new Mock<ILogger<MetricsCollector>>();
        _collector = new MetricsCollector(_mockPlatformProvider.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithEmptyChecks_ReturnsEmptyList()
    {
        // Arrange
        var checks = new List<PrtgCheck>();

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectMetricsAsync_WithCpuCheck_ReturnsCpuMetric()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetCpuUsageAsync())
            .ReturnsAsync(45.5);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "cpu",
                DisplayName = "CPU Usage",
                Threshold = new Threshold
                {
                    Warning = 70,
                    Critical = 85,
                    Operator = ">"
                }
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().HaveCount(1);
        var metric = result[0];
        metric.Name.Should().Be("cpu");
        metric.DisplayName.Should().Be("CPU Usage");
        metric.Value.Should().Be(45.5);
        metric.Unit.Should().Be("%");
        metric.Status.Should().Be(MetricStatus.Ok);
        metric.Threshold.Should().NotBeNull();
        metric.Threshold!.Warning.Should().Be(70);
        metric.Threshold.Critical.Should().Be(85);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithMemoryCheck_ReturnsMemoryMetric()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetMemoryUsageAsync())
            .ReturnsAsync(62.3);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "memory",
                DisplayName = "Memory Usage"
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().HaveCount(1);
        var metric = result[0];
        metric.Name.Should().Be("memory");
        metric.Value.Should().Be(62.3);
        metric.Status.Should().Be(MetricStatus.Ok);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithDiskCheck_ReturnsDiskMetric()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetDiskFreeSpaceAsync("C:"))
            .ReturnsAsync(35.7);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "disk",
                Target = "C:",
                DisplayName = "Disk C: Free Space"
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().HaveCount(1);
        var metric = result[0];
        metric.Name.Should().Be("disk");
        metric.Value.Should().Be(35.7);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithMultipleChecks_ReturnsAllMetrics()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetCpuUsageAsync()).ReturnsAsync(45.5);
        _mockPlatformProvider.Setup(p => p.GetMemoryUsageAsync()).ReturnsAsync(62.3);
        _mockPlatformProvider.Setup(p => p.GetDiskFreeSpaceAsync("C:")).ReturnsAsync(35.7);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck { Metric = "cpu", DisplayName = "CPU Usage" },
            new PrtgCheck { Metric = "memory", DisplayName = "Memory Usage" },
            new PrtgCheck { Metric = "disk", Target = "C:", DisplayName = "Disk Free Space" }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(m => m.Name == "cpu");
        result.Should().Contain(m => m.Name == "memory");
        result.Should().Contain(m => m.Name == "disk");
    }

    [Fact]
    public async Task CollectMetricsAsync_WithWarningThreshold_ReturnsWarningStatus()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetCpuUsageAsync())
            .ReturnsAsync(75.0);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "cpu",
                Threshold = new Threshold
                {
                    Warning = 70,
                    Critical = 85,
                    Operator = ">"
                }
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result[0].Status.Should().Be(MetricStatus.Warning);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithCriticalThreshold_ReturnsCriticalStatus()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetCpuUsageAsync())
            .ReturnsAsync(90.0);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "cpu",
                Threshold = new Threshold
                {
                    Warning = 70,
                    Critical = 85,
                    Operator = ">"
                }
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result[0].Status.Should().Be(MetricStatus.Critical);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithLessThanOperator_EvaluatesCorrectly()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetDiskFreeSpaceAsync("C:"))
            .ReturnsAsync(15.0); // Less than warning threshold

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck
            {
                Metric = "disk",
                Target = "C:",
                Threshold = new Threshold
                {
                    Warning = 20,
                    Critical = 10,
                    Operator = "<="
                }
            }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result[0].Status.Should().Be(MetricStatus.Warning);
    }

    [Fact]
    public async Task CollectMetricsAsync_WithUnknownMetricType_SkipsMetric()
    {
        // Arrange
        var checks = new List<PrtgCheck>
        {
            new PrtgCheck { Metric = "unknown", DisplayName = "Unknown Metric" }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectMetricsAsync_WhenProviderThrows_ContinuesWithOtherMetrics()
    {
        // Arrange
        _mockPlatformProvider.Setup(p => p.GetCpuUsageAsync())
            .ThrowsAsync(new Exception("CPU error"));
        _mockPlatformProvider.Setup(p => p.GetMemoryUsageAsync())
            .ReturnsAsync(62.3);

        var checks = new List<PrtgCheck>
        {
            new PrtgCheck { Metric = "cpu", DisplayName = "CPU Usage" },
            new PrtgCheck { Metric = "memory", DisplayName = "Memory Usage" }
        };

        // Act
        var result = await _collector.CollectMetricsAsync(checks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("memory");
    }

    [Fact]
    public void Dispose_ShouldDisposePlatformProvider()
    {
        // Act
        _collector.Dispose();

        // Assert
        _mockPlatformProvider.Verify(p => p.Dispose(), Times.Once);
    }
}
