using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.Services.PlatformMetrics;

namespace Minicon.SimpleAdmin.Tests.Services;

public sealed class CpuSamplingOptionsTests
{
    [Fact]
    public void Default_matches_documented_values()
    {
        CpuSamplingOptions.Default.WindowMilliseconds.Should().Be(60_000);
        CpuSamplingOptions.Default.Samples.Should().Be(12);
        CpuSamplingOptions.Default.SubtractSelfCpu.Should().BeTrue();
        CpuSamplingOptions.Default.RuntimeBudgetSeconds.Should().Be(240);
    }

    [Fact]
    public void Mean_averages_all_samples()
    {
        CpuSamplingOptions.Mean(new[] { 10.0, 100.0, 20.0, 30.0 }).Should().Be(40);
    }

    [Fact]
    public void Mean_returns_zero_for_empty_input()
    {
        CpuSamplingOptions.Mean(Array.Empty<double>()).Should().Be(0);
    }

    [Fact]
    public void CombineSamples_uses_the_average_so_a_single_burst_is_diluted()
    {
        // one 100 % burst in twelve 5 % samples → ≈ 12.9 %, not 100 %
        var samples = Enumerable.Repeat(5.0, 11).Append(100.0).ToArray();
        CpuSamplingOptions.CombineSamples(samples, 0, subtractSelf: true)
            .Should().BeApproximately(12.9167, 0.001);
    }

    [Fact]
    public void EffectiveWindow_uses_configured_window_when_budget_allows()
    {
        CpuSamplingOptions.Default.EffectiveWindow(TimeSpan.FromSeconds(20))
            .Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void EffectiveWindow_is_shortened_to_stay_within_budget()
    {
        // 240 s budget − 30 s reserve − 180 s elapsed = 30 s left
        CpuSamplingOptions.Default.EffectiveWindow(TimeSpan.FromSeconds(180))
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void EffectiveWindow_never_drops_below_minimum()
    {
        CpuSamplingOptions.Default.EffectiveWindow(TimeSpan.FromMinutes(10))
            .Should().Be(CpuSamplingOptions.MinimumWindow);
    }

    [Fact]
    public void EffectiveWindow_honours_custom_budget()
    {
        var o = CpuSamplingOptions.Default with { RuntimeBudgetSeconds = 60 };
        // 60 s − 30 s reserve − 10 s elapsed = 20 s
        o.EffectiveWindow(TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromSeconds(20));
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
        // mean = 80, self = 70 → effective 10
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
    public void CombineSamples_without_self_subtract_returns_pure_mean()
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
            ["Metrics:Cpu:SubtractSelf"] = "false",
            ["Metrics:Cpu:RuntimeBudgetSeconds"] = "120"
        }).Build();
        var o = CpuSamplingOptions.FromConfiguration(config);
        o.WindowMilliseconds.Should().Be(1500);
        o.Samples.Should().Be(5);
        o.SubtractSelfCpu.Should().BeFalse();
        o.RuntimeBudgetSeconds.Should().Be(120);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(50, 200)]
    [InlineData(500000, 180000)]
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
    [InlineData(1000, 120)]
    public void FromConfiguration_clamps_sample_count(int input, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metrics:Cpu:Samples"] = input.ToString()
        }).Build();
        CpuSamplingOptions.FromConfiguration(config).Samples.Should().Be(expected);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(100000, 3600)]
    public void FromConfiguration_clamps_runtime_budget(int input, int expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Metrics:Cpu:RuntimeBudgetSeconds"] = input.ToString()
        }).Build();
        CpuSamplingOptions.FromConfiguration(config).RuntimeBudgetSeconds.Should().Be(expected);
    }
}
