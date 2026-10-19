using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics.Windows;

/// <summary>
/// Windows-specific metrics provider using Performance Counters
/// </summary>
public class WindowsMetricsProvider : IPlatformMetricsProvider
{
    private readonly ILogger _logger;
    private readonly CpuSamplingOptions _cpuOptions;
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the WindowsMetricsProvider class
    /// </summary>
    public WindowsMetricsProvider(ILogger logger)
        : this(logger, CpuSamplingOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance with explicit CPU-sampling options (tests + hosts
    /// that want a different sampling window / self-subtract behaviour).
    /// </summary>
    public WindowsMetricsProvider(ILogger logger, CpuSamplingOptions cpuOptions)
    {
        _logger = logger;
        _cpuOptions = cpuOptions;
    }

    /// <summary>
    /// Checks if this provider is supported on the current platform
    /// </summary>
    public bool IsSupported()
    {
        return OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Initializes Windows Performance Counters
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _logger.LogInformation("Initializing Windows Performance Counters");

        try
        {
            // CPU counter
            _counters["cpu"] = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _logger.LogDebug("CPU performance counter initialized");

            // Memory counter
            _counters["memory"] = new PerformanceCounter("Memory", "% Committed Bytes In Use", true);
            _logger.LogDebug("Memory performance counter initialized");

            // Disk counter for C: drive
            _counters["disk_c"] = new PerformanceCounter("LogicalDisk", "% Free Space", "C:", true);
            _logger.LogDebug("Disk C: performance counter initialized");

            _initialized = true;
            _logger.LogInformation("Windows Performance Counters initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Windows Performance Counters");
            throw;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets system CPU usage percentage. Samples the <c>% Processor Time</c> rate
    /// counter multiple times over a configurable window (default 900 ms, 3 samples)
    /// and returns the median — a single 100 ms snapshot easily lands inside a
    /// JIT/DI/checker burst and read 100 % even on an idle box. On top of that,
    /// the worker's own CPU share over the same window is subtracted, so the
    /// metrics collector does not measure itself.
    /// </summary>
    public async Task<double> GetCpuUsageAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        if (!_counters.TryGetValue("cpu", out var counter))
        {
            _logger.LogWarning("CPU counter not available");
            return 0;
        }

        try
        {
            // Discard the first sample (always 0 / stale) and prime the counter.
            counter.NextValue();

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var swStart = System.Diagnostics.Stopwatch.StartNew();

            var samples = new List<double>(_cpuOptions.Samples);
            var perSampleDelay = Math.Max(50, _cpuOptions.WindowMilliseconds / _cpuOptions.Samples);
            for (var i = 0; i < _cpuOptions.Samples; i++)
            {
                await Task.Delay(perSampleDelay);
                samples.Add(counter.NextValue());
            }

            swStart.Stop();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            double selfPercent = 0;
            if (swStart.Elapsed.TotalMilliseconds > 0 && Environment.ProcessorCount > 0)
            {
                selfPercent = 100.0 * (cpuAfter - cpuBefore).TotalMilliseconds
                              / swStart.Elapsed.TotalMilliseconds
                              / Environment.ProcessorCount;
            }

            var effective = CpuSamplingOptions.CombineSamples(samples, selfPercent, _cpuOptions.SubtractSelfCpu);
            _logger.LogDebug(
                "CPU usage: {Value:F1}% (system median {System:F1}%, self {Self:F1}% over {Elapsed} ms, samples [{Samples}])",
                effective,
                CpuSamplingOptions.Median(samples),
                selfPercent,
                swStart.Elapsed.TotalMilliseconds,
                string.Join(", ", samples.Select(s => s.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            return effective;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading CPU counter");
            return 0;
        }
    }

    /// <summary>
    /// Gets memory usage percentage using Performance Counter
    /// </summary>
    public async Task<double> GetMemoryUsageAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        if (_counters.TryGetValue("memory", out var counter))
        {
            try
            {
                await Task.Delay(10);
                var value = counter.NextValue();
                _logger.LogTrace("Memory usage: {Value}%", value);
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading memory counter");
                return 0;
            }
        }

        _logger.LogWarning("Memory counter not available");
        return 0;
    }

    /// <summary>
    /// Gets disk free space percentage using Performance Counter
    /// </summary>
    public async Task<double> GetDiskFreeSpaceAsync(string drive)
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }

        var counterKey = $"disk_{drive.ToLower().Replace(":", "")}";

        // Try to get existing counter
        if (_counters.TryGetValue(counterKey, out var counter))
        {
            try
            {
                await Task.Delay(10);
                var value = counter.NextValue();
                _logger.LogTrace("Disk {Drive} free space: {Value}%", drive, value);
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading disk counter for {Drive}", drive);
            }
        }
        else
        {
            // Try to create counter for this drive
            try
            {
                _logger.LogDebug("Creating performance counter for drive {Drive}", drive);
                var newCounter = new PerformanceCounter("LogicalDisk", "% Free Space", drive, true);
                _counters[counterKey] = newCounter;

                await Task.Delay(10);
                var value = newCounter.NextValue();
                _logger.LogTrace("Disk {Drive} free space: {Value}%", drive, value);
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create performance counter for drive {Drive}, using fallback", drive);
            }
        }

        // Fallback: use DriveInfo
        try
        {
            var driveInfo = new DriveInfo(drive);
            if (driveInfo.IsReady)
            {
                var freeSpacePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;
                _logger.LogTrace("Disk {Drive} free space (fallback): {Value}%", drive, freeSpacePercent);
                return freeSpacePercent;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting disk info for {Drive}", drive);
        }

        return 0;
    }

    /// <summary>
    /// Disposes performance counters
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebug("Disposing Windows Performance Counters");

        foreach (var counter in _counters.Values)
        {
            counter?.Dispose();
        }

        _counters.Clear();
        _initialized = false;

        _logger.LogInformation("Windows Performance Counters disposed");
    }
}
