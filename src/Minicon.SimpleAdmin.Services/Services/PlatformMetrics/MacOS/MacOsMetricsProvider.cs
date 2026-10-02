using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Services.PlatformMetrics.MacOS;

/// <summary>
/// macOS-specific metrics provider using system commands
/// </summary>
public class MacOsMetricsProvider : IPlatformMetricsProvider
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the MacOsMetricsProvider class
    /// </summary>
    public MacOsMetricsProvider(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if this provider is supported on the current platform
    /// </summary>
    public bool IsSupported()
    {
        return OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// Initializes macOS-specific resources
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing macOS metrics provider");

        // Verify that required commands are available
        if (!await IsCommandAvailableAsync("top"))
        {
            _logger.LogWarning("'top' command not available");
        }

        if (!await IsCommandAvailableAsync("vm_stat"))
        {
            _logger.LogWarning("'vm_stat' command not available");
        }

        _logger.LogInformation("macOS metrics provider initialized successfully");
    }

    /// <summary>
    /// Gets CPU usage percentage using 'top' command
    /// </summary>
    public async Task<double> GetCpuUsageAsync()
    {
        try
        {
            // Use top command to get CPU usage
            // top -l 1 -n 0 gives a single sample
            var output = await ExecuteCommandAsync("top", "-l 1 -n 0");

            // Parse output to find CPU usage line
            // Format: "CPU usage: 12.34% user, 5.67% sys, 82.00% idle"
            var lines = output.Split('\n');
            var cpuLine = lines.FirstOrDefault(l => l.Contains("CPU usage:"));

            if (string.IsNullOrEmpty(cpuLine))
            {
                _logger.LogWarning("Could not find CPU usage line in top output");
                return 0;
            }

            // Extract idle percentage
            var idleMatch = System.Text.RegularExpressions.Regex.Match(cpuLine, @"(\d+\.?\d*)%\s+idle");
            if (!idleMatch.Success)
            {
                _logger.LogWarning("Could not parse idle percentage from: {Line}", cpuLine);
                return 0;
            }

            if (double.TryParse(idleMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var idlePercent))
            {
                var cpuUsage = 100 - idlePercent;
                _logger.LogTrace("CPU usage: {Value}%", cpuUsage);
                return Math.Max(0, Math.Min(100, cpuUsage)); // Clamp between 0-100
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting CPU usage");
            return 0;
        }
    }

    /// <summary>
    /// Gets memory usage percentage using 'vm_stat' command
    /// </summary>
    public async Task<double> GetMemoryUsageAsync()
    {
        try
        {
            var output = await ExecuteCommandAsync("vm_stat", "");

            // Parse vm_stat output
            // Pages free, active, inactive, speculative, wired, etc.
            var lines = output.Split('\n');
            long pagesFree = 0;
            long pagesActive = 0;
            long pagesInactive = 0;
            long pagesWired = 0;
            long pagesSpeculative = 0;

            foreach (var line in lines)
            {
                if (line.Contains("Pages free:"))
                {
                    pagesFree = ParseVmStatLine(line);
                }
                else if (line.Contains("Pages active:"))
                {
                    pagesActive = ParseVmStatLine(line);
                }
                else if (line.Contains("Pages inactive:"))
                {
                    pagesInactive = ParseVmStatLine(line);
                }
                else if (line.Contains("Pages wired down:"))
                {
                    pagesWired = ParseVmStatLine(line);
                }
                else if (line.Contains("Pages speculative:"))
                {
                    pagesSpeculative = ParseVmStatLine(line);
                }
            }

            // Calculate memory usage
            // Used memory = wired + active + inactive
            // Free memory = free + speculative
            long usedPages = pagesWired + pagesActive + pagesInactive;
            long totalPages = usedPages + pagesFree + pagesSpeculative;

            if (totalPages == 0)
            {
                _logger.LogWarning("Could not calculate total memory pages");
                return 0;
            }

            var memoryUsage = (double)usedPages / totalPages * 100;
            _logger.LogTrace("Memory usage: {Value}%", memoryUsage);

            return Math.Max(0, Math.Min(100, memoryUsage)); // Clamp between 0-100
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory usage");
            return 0;
        }
    }

    /// <summary>
    /// Gets disk free space percentage using DriveInfo
    /// </summary>
    public async Task<double> GetDiskFreeSpaceAsync(string drive)
    {
        try
        {
            // On macOS, drive is typically "/" or a mount point
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
    /// Executes a shell command and returns output
    /// </summary>
    private async Task<string> ExecuteCommandAsync(string command, string arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            _logger.LogWarning("Command '{Command} {Arguments}' failed with exit code {ExitCode}: {Error}",
                command, arguments, process.ExitCode, error);
        }

        return output;
    }

    /// <summary>
    /// Checks if a command is available on the system
    /// </summary>
    private async Task<bool> IsCommandAvailableAsync(string command)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a line from vm_stat output to extract page count
    /// </summary>
    private long ParseVmStatLine(string line)
    {
        // Format: "Pages free:                              12345."
        var parts = line.Split(':');
        if (parts.Length < 2)
        {
            return 0;
        }

        var valuePart = parts[1].Trim().TrimEnd('.');
        return long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// Disposes resources
    /// </summary>
    public void Dispose()
    {
        _logger.LogDebug("Disposing macOS metrics provider");
        // No resources to dispose
    }
}
