using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics.Linux;

/// <summary>
/// Linux-specific metrics provider using /proc filesystem
/// </summary>
public class LinuxMetricsProvider : IPlatformMetricsProvider
{
    private readonly ILogger _logger;
    private CpuStats? _previousCpuStats;
    private DateTime _lastCpuCheck;

    /// <summary>
    /// Initializes a new instance of the LinuxMetricsProvider class
    /// </summary>
    public LinuxMetricsProvider(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if this provider is supported on the current platform
    /// </summary>
    public bool IsSupported()
    {
        return OperatingSystem.IsLinux();
    }

    /// <summary>
    /// Initializes Linux-specific resources
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing Linux metrics provider");

        // Verify /proc filesystem is accessible
        if (!Directory.Exists("/proc"))
        {
            _logger.LogError("/proc filesystem not found");
            throw new InvalidOperationException("/proc filesystem not accessible");
        }

        // Perform initial CPU stats collection for delta calculation
        _previousCpuStats = await ReadCpuStatsAsync();
        _lastCpuCheck = DateTime.UtcNow;

        _logger.LogInformation("Linux metrics provider initialized successfully");
    }

    /// <summary>
    /// Gets CPU usage percentage by reading /proc/stat
    /// </summary>
    public async Task<double> GetCpuUsageAsync()
    {
        try
        {
            var currentStats = await ReadCpuStatsAsync();
            var now = DateTime.UtcNow;

            if (_previousCpuStats == null)
            {
                _previousCpuStats = currentStats;
                _lastCpuCheck = now;
                // Return 0 on first call since we need two measurements
                return 0;
            }

            var totalDiff = currentStats.Total - _previousCpuStats.Total;
            var idleDiff = currentStats.Idle - _previousCpuStats.Idle;

            // Calculate CPU usage percentage
            double cpuUsage = 0;
            if (totalDiff > 0)
            {
                cpuUsage = (1.0 - (double)idleDiff / totalDiff) * 100;
            }

            // Update for next call
            _previousCpuStats = currentStats;
            _lastCpuCheck = now;

            _logger.LogTrace("CPU usage: {Value}%", cpuUsage);
            return Math.Max(0, Math.Min(100, cpuUsage)); // Clamp between 0-100
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading CPU stats from /proc/stat");
            return 0;
        }
    }

    /// <summary>
    /// Gets memory usage percentage by reading /proc/meminfo
    /// </summary>
    public async Task<double> GetMemoryUsageAsync()
    {
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/meminfo");
            long memTotal = 0;
            long memAvailable = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("MemTotal:"))
                {
                    memTotal = ParseMemInfoLine(line);
                }
                else if (line.StartsWith("MemAvailable:"))
                {
                    memAvailable = ParseMemInfoLine(line);
                }

                if (memTotal > 0 && memAvailable > 0)
                {
                    break;
                }
            }

            if (memTotal == 0)
            {
                _logger.LogWarning("Could not read MemTotal from /proc/meminfo");
                return 0;
            }

            var memoryUsage = (1.0 - (double)memAvailable / memTotal) * 100;
            _logger.LogTrace("Memory usage: {Value}% (Total: {Total} KB, Available: {Available} KB)",
                memoryUsage, memTotal, memAvailable);

            return Math.Max(0, Math.Min(100, memoryUsage)); // Clamp between 0-100
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading memory stats from /proc/meminfo");
            return 0;
        }
    }

    /// <summary>
    /// Gets disk free space percentage using statvfs
    /// </summary>
    public async Task<double> GetDiskFreeSpaceAsync(string drive)
    {
        try
        {
            // On Linux, drive is a mount point like "/" or "/home"
            var driveInfo = new DriveInfo(drive);

            if (!driveInfo.IsReady)
            {
                _logger.LogWarning("Drive {Drive} is not ready", drive);
                return 0;
            }

            var freeSpacePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;
            _logger.LogTrace("Disk {Drive} free space: {Value}%", drive, freeSpacePercent);

            return Math.Max(0, Math.Min(100, freeSpacePercent)); // Clamp between 0-100
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting disk info for {Drive}", drive);
            return 0;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Reads CPU statistics from /proc/stat
    /// </summary>
    private async Task<CpuStats> ReadCpuStatsAsync()
    {
        var lines = await File.ReadAllLinesAsync("/proc/stat");
        var cpuLine = lines.FirstOrDefault(l => l.StartsWith("cpu "));

        if (string.IsNullOrEmpty(cpuLine))
        {
            throw new InvalidOperationException("Could not find CPU line in /proc/stat");
        }

        var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            throw new InvalidOperationException("Invalid CPU line format in /proc/stat");
        }

        // CPU line format: cpu user nice system idle iowait irq softirq...
        var user = long.Parse(parts[1], CultureInfo.InvariantCulture);
        var nice = long.Parse(parts[2], CultureInfo.InvariantCulture);
        var system = long.Parse(parts[3], CultureInfo.InvariantCulture);
        var idle = long.Parse(parts[4], CultureInfo.InvariantCulture);
        var iowait = parts.Length > 5 ? long.Parse(parts[5], CultureInfo.InvariantCulture) : 0;
        var irq = parts.Length > 6 ? long.Parse(parts[6], CultureInfo.InvariantCulture) : 0;
        var softirq = parts.Length > 7 ? long.Parse(parts[7], CultureInfo.InvariantCulture) : 0;

        var total = user + nice + system + idle + iowait + irq + softirq;

        return new CpuStats
        {
            Total = total,
            Idle = idle
        };
    }

    /// <summary>
    /// Parses a line from /proc/meminfo to extract KB value
    /// </summary>
    private long ParseMemInfoLine(string line)
    {
        // Format: "MemTotal:       16384000 kB"
        var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }

        var valuePart = parts[1].Trim().Split(' ')[0];
        return long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// Disposes resources
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebug("Disposing Linux metrics provider");
        // No resources to dispose
    }

    /// <summary>
    /// Internal structure for CPU statistics
    /// </summary>
    private class CpuStats
    {
        public long Total { get; init; }
        public long Idle { get; init; }
    }
}
