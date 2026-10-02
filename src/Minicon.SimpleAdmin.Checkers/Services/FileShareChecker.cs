using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Watches file shares for "stuck" files (too old / present after a daily cutoff) and scans log files
/// for configured error signatures. The IO is behind <see cref="IFileSystemAccess"/>; the evaluation is
/// pure and unit-testable. Any unreachable path is reported via <see cref="FileMonitoringState.AnyError"/>.
/// </summary>
public interface IFileMonitoringChecker
{
    Task<FileMonitoringState> CheckAsync(ServerFileMonitoringConfig config, FileMonitoringDefaults defaults, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class FileShareChecker(IFileSystemAccess fileSystem, ILogger<FileShareChecker> logger) : IFileMonitoringChecker
{
    public Task<FileMonitoringState> CheckAsync(ServerFileMonitoringConfig config, FileMonitoringDefaults defaults, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(config, defaults, DateTime.Now, cancellationToken), cancellationToken);

    /// <summary>Synchronous core with an injected <paramref name="now"/> for deterministic testing.</summary>
    internal FileMonitoringState Check(ServerFileMonitoringConfig config, FileMonitoringDefaults defaults, DateTime now, CancellationToken cancellationToken = default)
    {
        var state = new FileMonitoringState();

        // Business-hours gate (reuses the TimeProfile schedule syntax, e.g. "Mon-Fri 06:00-22:00").
        if (!string.IsNullOrWhiteSpace(config.Schedule)
            && !new TimeProfile { Schedule = config.Schedule! }.IsActiveAt(now))
        {
            state.OutsideBusinessHours = true;
            return state;
        }

        foreach (var dir in config.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir.Path))
            {
                continue;
            }

            state.DirectoriesChecked++;
            try
            {
                var files = fileSystem.EnumerateFiles(dir.Path, dir.Recursive);
                state.StuckFiles.AddRange(EvaluateDirectory(files, dir, defaults, now));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FileMonitoring: Verzeichnis '{Path}' nicht erreichbar", dir.Path);
                if (!state.AnyError)
                {
                    state.AnyError = true;
                    state.Error = $"{(string.IsNullOrEmpty(dir.Name) ? dir.Path : dir.Name)}: {ex.Message}";
                }
            }
        }

        foreach (var scan in config.LogScans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(scan.Path) || scan.Patterns.Count == 0)
            {
                continue;
            }

            try
            {
                var resolved = fileSystem.ResolveLogFiles(scan.Path);
                if (scan.MaxFileAgeMinutes is int maxAge and > 0)
                {
                    resolved = resolved.Where(f => (now - f.LastWriteTime).TotalMinutes <= maxAge).ToList();
                }

                var lines = new List<string>();
                foreach (var file in resolved)
                {
                    state.LogFilesScanned++;
                    lines.AddRange(fileSystem.ReadLines(file.FullPath, scan.MaxLines));
                }

                var sampleLogPath = resolved.Count > 0 ? resolved[0].FullPath : scan.Path;
                state.LogMatches.AddRange(EvaluateLogContent(
                    scan.Name, sampleLogPath, lines, scan.Patterns, scan.WikiUrl, scan.RemediationSteps, logger));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FileMonitoring: Log-Scan '{Path}' fehlgeschlagen", scan.Path);
                if (!state.AnyError)
                {
                    state.AnyError = true;
                    state.Error = $"{(string.IsNullOrEmpty(scan.Name) ? scan.Path : scan.Name)}: {ex.Message}";
                }
            }
        }

        // Keep the payload compact (this is serialized into the HTTP-published status JSON).
        if (state.StuckFiles.Count > defaults.MaxReportedItems)
        {
            state.StuckFiles = state.StuckFiles
                .OrderByDescending(s => s.IsCritical).ThenByDescending(s => s.AgeMinutes)
                .Take(defaults.MaxReportedItems).ToList();
        }

        if (state.LogMatches.Count > defaults.MaxReportedItems)
        {
            state.LogMatches = state.LogMatches
                .OrderByDescending(m => m.IsCritical).ThenByDescending(m => m.Count)
                .Take(defaults.MaxReportedItems).ToList();
        }

        return state;
    }

    // --- pure evaluation (testable) ---

    /// <summary>Flags files that are older than the configured age threshold and/or present after the daily cutoff.</summary>
    internal static IEnumerable<StuckFile> EvaluateDirectory(
        IReadOnlyList<FileEntry> files,
        WatchedDirectory dir,
        FileMonitoringDefaults defaults,
        DateTime now)
    {
        var warnT = dir.AgeMinutesWarning ?? defaults.StuckFileAgeMinutesWarning;
        var critT = dir.AgeMinutesCritical ?? defaults.StuckFileAgeMinutesCritical;
        var cutoff = ParseTime(dir.CutoffTime);
        var cutoffActive = cutoff.HasValue && now.TimeOfDay >= cutoff.Value;
        var cutoffCritical = string.Equals(dir.CutoffSeverity, "Critical", StringComparison.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            if (dir.IncludePatterns.Count > 0 && !MatchesAny(f.Name, dir.IncludePatterns))
            {
                continue;
            }

            if (dir.ExcludePatterns.Count > 0 && MatchesAny(f.Name, dir.ExcludePatterns))
            {
                continue;
            }

            var ageMin = (now - f.LastWriteTime).TotalMinutes;
            var ageWarn = warnT > 0 && ageMin >= warnT;
            var ageCrit = critT > 0 && ageMin >= critT;

            if (!ageWarn && !ageCrit && !cutoffActive)
            {
                continue;
            }

            var isCritical = ageCrit || (cutoffActive && cutoffCritical);
            var reason = ageCrit ? "age"
                : cutoffActive && cutoffCritical ? "cutoff"
                : ageWarn ? "age"
                : "cutoff";

            yield return new StuckFile
            {
                Directory = dir.Name,
                FilePath = f.FullPath,
                LastModified = f.LastWriteTime,
                AgeMinutes = Math.Round(ageMin, 1),
                Reason = reason,
                IsCritical = isCritical,
                WikiUrl = dir.WikiUrl,
                RemediationSteps = new List<string>(dir.RemediationSteps)
            };
        }
    }

    /// <summary>Counts log lines matching each configured signature and yields one aggregated <see cref="LogMatch"/> per hit pattern.</summary>
    internal static IEnumerable<LogMatch> EvaluateLogContent(
        string logScanName,
        string logPath,
        IReadOnlyList<string> lines,
        IReadOnlyList<LogPatternConfig> patterns,
        string? defaultWikiUrl,
        List<string> defaultSteps,
        ILogger? logger = null)
    {
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p.Regex))
            {
                continue;
            }

            Regex regex;
            try
            {
                regex = new Regex(p.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                logger?.LogWarning(ex, "FileMonitoring: ungültiges Regex '{Pattern}' in Log-Scan '{Scan}'", p.Regex, logScanName);
                continue;
            }

            var count = 0;
            var sample = string.Empty;
            foreach (var line in lines)
            {
                if (!regex.IsMatch(line))
                {
                    continue;
                }

                count++;
                if (sample.Length == 0)
                {
                    sample = line.Trim();
                }
            }

            if (count == 0)
            {
                continue;
            }

            yield return new LogMatch
            {
                LogScan = logScanName,
                LogPath = logPath,
                Pattern = string.IsNullOrEmpty(p.Name) ? p.Regex : p.Name,
                Count = count,
                SampleLine = sample,
                IsCritical = string.Equals(p.Severity, "Critical", StringComparison.OrdinalIgnoreCase),
                WikiUrl = string.IsNullOrEmpty(p.WikiUrl) ? defaultWikiUrl : p.WikiUrl,
                RemediationSteps = p.RemediationSteps.Count > 0
                    ? new List<string>(p.RemediationSteps)
                    : new List<string>(defaultSteps)
            };
        }
    }

    private static bool MatchesAny(string name, List<string> patterns)
        => patterns.Any(pattern =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    private static TimeSpan? ParseTime(string? value)
        => TimeSpan.TryParse(value, out var ts) ? ts : null;
}
