using Microsoft.Extensions.Configuration;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics;

/// <summary>
/// Tuning knobs for the system-CPU sampling: a longer window with multiple samples
/// (median) rules out short bursts, and subtracting the worker's own CPU keeps the
/// measurement from being contaminated by the metrics collector itself.
/// </summary>
public sealed record CpuSamplingOptions(
    int WindowMilliseconds,
    int Samples,
    bool SubtractSelfCpu)
{
    public const int DefaultWindowMilliseconds = 900;
    public const int DefaultSamples = 3;
    public const bool DefaultSubtractSelfCpu = true;

    public static CpuSamplingOptions Default { get; } =
        new(DefaultWindowMilliseconds, DefaultSamples, DefaultSubtractSelfCpu);

    /// <summary>
    /// Reads <c>Metrics:Cpu:WindowMs</c> / <c>Metrics:Cpu:Samples</c> / <c>Metrics:Cpu:SubtractSelf</c>
    /// from the host configuration. Falls back to <see cref="Default"/> when a key
    /// is missing, clamps all values into sane ranges (window ≥ 200 ms, samples ≥ 2).
    /// </summary>
    public static CpuSamplingOptions FromConfiguration(IConfiguration? configuration)
    {
        var windowMs = configuration?.GetValue<int?>("Metrics:Cpu:WindowMs") ?? DefaultWindowMilliseconds;
        var samples = configuration?.GetValue<int?>("Metrics:Cpu:Samples") ?? DefaultSamples;
        var subtract = configuration?.GetValue<bool?>("Metrics:Cpu:SubtractSelf") ?? DefaultSubtractSelfCpu;

        if (windowMs < 200) windowMs = 200;
        if (windowMs > 10_000) windowMs = 10_000;
        if (samples < 2) samples = 2;
        if (samples > 20) samples = 20;

        return new CpuSamplingOptions(windowMs, samples, subtract);
    }

    /// <summary>Median of an unordered list of sample values. Returns 0 for empty input.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Final system CPU percentage: median of the system samples, minus the worker's
    /// own CPU share over the same window, clamped into [0, 100]. <paramref name="selfPercent"/>
    /// is <c>100 × selfCpuTime / wallClockTime / coreCount</c>.
    /// </summary>
    public static double CombineSamples(IReadOnlyList<double> systemSamples, double selfPercent, bool subtractSelf)
    {
        var system = Median(systemSamples);
        var effective = subtractSelf ? system - Math.Max(0, selfPercent) : system;
        if (effective < 0) effective = 0;
        if (effective > 100) effective = 100;
        return effective;
    }
}
