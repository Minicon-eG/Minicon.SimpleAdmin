using Microsoft.Extensions.Configuration;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics;

/// <summary>
/// Tuning knobs for the system-CPU sampling. The worker measures the CPU after all
/// other checkers have finished and averages it over a long window (default 60 s),
/// like Checkmk/Zabbix report an interval average instead of a snapshot. The window
/// is shortened so the whole worker run stays inside <see cref="RuntimeBudgetSeconds"/>.
/// Subtracting the worker's own CPU keeps the collector from measuring itself.
/// </summary>
public sealed record CpuSamplingOptions(
    int WindowMilliseconds,
    int Samples,
    bool SubtractSelfCpu)
{
    public const int DefaultWindowMilliseconds = 60_000;
    public const int DefaultSamples = 12;
    public const bool DefaultSubtractSelfCpu = true;
    public const int DefaultRuntimeBudgetSeconds = 240;

    /// <summary>Time kept free after the CPU measurement (status files, HTML, notifier).</summary>
    public static readonly TimeSpan RuntimeReserve = TimeSpan.FromSeconds(30);

    /// <summary>Shortest window used when the runtime budget is already (almost) exhausted.</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum total runtime of the worker process in seconds. The CPU window is
    /// shortened so that process start + checkers + CPU window + <see cref="RuntimeReserve"/>
    /// stays within this budget.
    /// </summary>
    public int RuntimeBudgetSeconds { get; init; } = DefaultRuntimeBudgetSeconds;

    public static CpuSamplingOptions Default { get; } =
        new(DefaultWindowMilliseconds, DefaultSamples, DefaultSubtractSelfCpu);

    /// <summary>
    /// Reads <c>Metrics:Cpu:WindowMs</c> / <c>Metrics:Cpu:Samples</c> / <c>Metrics:Cpu:SubtractSelf</c> /
    /// <c>Metrics:Cpu:RuntimeBudgetSeconds</c> from the host configuration. Falls back to
    /// <see cref="Default"/> when a key is missing, clamps all values into sane ranges
    /// (window 200 ms … 180 s, samples 2 … 120, budget 30 s … 1 h).
    /// </summary>
    public static CpuSamplingOptions FromConfiguration(IConfiguration? configuration)
    {
        var windowMs = configuration?.GetValue<int?>("Metrics:Cpu:WindowMs") ?? DefaultWindowMilliseconds;
        var samples = configuration?.GetValue<int?>("Metrics:Cpu:Samples") ?? DefaultSamples;
        var subtract = configuration?.GetValue<bool?>("Metrics:Cpu:SubtractSelf") ?? DefaultSubtractSelfCpu;
        var budget = configuration?.GetValue<int?>("Metrics:Cpu:RuntimeBudgetSeconds") ?? DefaultRuntimeBudgetSeconds;

        windowMs = Math.Clamp(windowMs, 200, 180_000);
        samples = Math.Clamp(samples, 2, 120);
        budget = Math.Clamp(budget, 30, 3600);

        return new CpuSamplingOptions(windowMs, samples, subtract) { RuntimeBudgetSeconds = budget };
    }

    /// <summary>
    /// Sampling window for this run: the configured window, shortened when the worker
    /// has already used most of its runtime budget (<paramref name="elapsedSinceStart"/>
    /// = time since process start), never shorter than <see cref="MinimumWindow"/>.
    /// </summary>
    public TimeSpan EffectiveWindow(TimeSpan elapsedSinceStart)
    {
        var configured = TimeSpan.FromMilliseconds(WindowMilliseconds);
        var remaining = TimeSpan.FromSeconds(RuntimeBudgetSeconds) - RuntimeReserve - elapsedSinceStart;
        var window = remaining < configured ? remaining : configured;
        return window < MinimumWindow ? MinimumWindow : window;
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

    /// <summary>Arithmetic mean of the sample values. Returns 0 for empty input.</summary>
    public static double Mean(IReadOnlyList<double> values)
        => values.Count == 0 ? 0 : values.Average();

    /// <summary>
    /// Final system CPU percentage: mean of the system samples (each sample of the
    /// <c>% Processor Time</c> rate counter is the average since the previous one, so
    /// the mean is the average over the whole window), minus the worker's own CPU
    /// share over the same window, clamped into [0, 100]. <paramref name="selfPercent"/>
    /// is <c>100 × selfCpuTime / wallClockTime / coreCount</c>.
    /// </summary>
    public static double CombineSamples(IReadOnlyList<double> systemSamples, double selfPercent, bool subtractSelf)
    {
        var system = Mean(systemSamples);
        var effective = subtractSelf ? system - Math.Max(0, selfPercent) : system;
        return Math.Clamp(effective, 0, 100);
    }
}
