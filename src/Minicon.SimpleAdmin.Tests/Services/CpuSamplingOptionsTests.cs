using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.Services.PlatformMetrics;

namespace Minicon.SimpleAdmin.Tests.Services;

public sealed class CpuSamplingOptionsTests
{
    [Fact]
    public void Default_matches_documented_values()
    {
        CpuSamplingOptions.Default.WindowMilliseconds.Should().Be(900);
        CpuSamplingOptions.Default.Samples.Should().Be(3);
        CpuSamplingOptions.Default.SubtractSelfCpu.Should().BeTrue();
    }

    [Fact]
    public void Median_returns_middle_value_for_odd_count()
    {
        CpuSamplingOptions.Median(new[] { 10.0, 100.0, 20.0 }).Should().Be(20);
    }

    [Fact]
    public void Median_averages_middle_two_for_even_count()
    {
        CpuSamplingOptions.Median(new[] { 10.0, 100.0, 20.0, 40.0 }).Should().Be(30);
    }

    [Fact]
    public void Median_returns_zero_for_empty_input()
    {
        CpuSamplingOptions.Median(Array.Empty<double>()).Should().Be(0);
    }

    [Fact]
    public void CombineSamples_subtracts_self_cpu_and_clamps()
    {
        // median = 80, self = 70 → effective 10
        CpuSamplingOptions.CombineSamples(new[] { 80.0, 80.0, 80.0 }, 70.0, subtractSelf: true)
            .Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void CombineSamples_clamps_negative_result_to_zero()
    {
        CpuSamplingOptions.CombineSamples(new[] { 5.0 }, 90.0, subtractSelf: true).Should().Be(0);
    }

    [Fact]
    public void CombineSamples_clamps_above_hundred()
    {
        CpuSamplingOptions.CombineSamples(new[] { 150.0 }, 0, subtractSelf: true).Should().Be(100);
    }

    [Fact]
    public void CombineSamples_without_self_subtract_returns_pure_median()
    {
        CpuSamplingOptions.CombineSamples(new[] { 80.0, 80.0, 80.0 }, 70.0, subtractSelf: false)
            .Should().Be(80);
    }

    [Fact]
    public void FromConfiguration_uses_defaults_when_missing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var o = CpuSamplingOptions.FromConfiguration(config);
        o.Should().Be(CpuSamplingOptions.Default);
    }

    [Fact]
    public void FromConfiguration_reads_overrides()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metrics:Cpu:WindowMs"] = "1500",
            ["Metrics:Cpu:Samples"] = "5",
            ["Metrics:Cpu:SubtractSelf"] = "false"
        }).Build();
        var o = CpuSamplingOptions.FromConfiguration(config);
        o.WindowMilliseconds.Should().Be(1500);
        o.Samples.Should().Be(5);
        o.SubtractSelfCpu.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(50, 200)]
    [InlineData(20000, 10000)]
    public void FromConfiguration_clamps_window(int input, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metrics:Cpu:WindowMs"] = input.ToString()
        }).Build();
        CpuSamplingOptions.FromConfiguration(config).WindowMilliseconds.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(100, 20)]
    public void FromConfiguration_clamps_sample_count(int input, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metrics:Cpu:Samples"] = input.ToString()
        }).Build();
        CpuSamplingOptions.FromConfiguration(config).Samples.Should().Be(expected);
    }
}
