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
            if (string.IsNullOrWhiteSpace(scan.Path) || (scan.Patterns.Count == 0 && scan.PendingRules.Count == 0 && scan.ExpectedRuns.Count == 0))
            {
                continue;
            }

            try
            {
                var resolved = fileSystem.ResolveLogFiles(scan.Path);

                var signatureFiles = scan.Patterns.Count > 0
                    ? ApplyAgeFilter(resolved, scan, now)
                    : Array.Empty<FileEntry>();
                var runFile = scan.PendingRules.Count > 0 || scan.ExpectedRuns.Count > 0
                    ? NewestFile(resolved)
                    : null;
                var tails = ReadFileTails(scan, signatureFiles, runFile);

                var lines = new List<string>();
                foreach (var file in signatureFiles)
                {
                    state.LogFilesScanned++;
                    var tail = TailOf(tails, file.FullPath);
                    lines.AddRange(scan.MaxLines > 0 ? tail.TakeLast(scan.MaxLines) : tail);
                }

                var sampleLogPath = signatureFiles.Count > 0 ? signatureFiles[0].FullPath : scan.Path;
                state.LogMatches.AddRange(EvaluateLogContent(
                    scan.Name, sampleLogPath, lines, scan.Patterns, scan.WikiUrl, scan.RemediationSteps, logger));

                EvaluatePendingRules(scan, resolved, tails, now, state, logger);
                EvaluateExpectedRuns(scan, resolved, tails, now, state, logger);
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

        if (state.PendingRuns.Count > defaults.MaxReportedItems)
        {
            state.PendingRuns = state.PendingRuns
                .OrderByDescending(p => p.IsCritical).ThenByDescending(p => p.AgeMinutes)
                .Take(defaults.MaxReportedItems).ToList();
        }

        if (state.RunningLogRuns.Count > defaults.MaxReportedItems)
        {
            state.RunningLogRuns = state.RunningLogRuns
                .OrderByDescending(r => r.IsCritical).ThenByDescending(r => r.AgeMinutes)
                .Take(defaults.MaxReportedItems).ToList();
        }

        if (state.AbandonedRuns.Count > defaults.MaxReportedItems)
        {
            state.AbandonedRuns = state.AbandonedRuns
                .OrderByDescending(a => a.IsCritical).ThenByDescending(a => a.Count)
                .Take(defaults.MaxReportedItems).ToList();
        }

        if (state.MissingRuns.Count > defaults.MaxReportedItems)
        {
            state.MissingRuns = state.MissingRuns
                .OrderByDescending(m => m.IsCritical).ThenByDescending(m => m.ExpectedAt)
                .Take(defaults.MaxReportedItems).ToList();
        }

        return state;
    }

    /// <summary>Evaluates every open-run rule against only the newest matching file.</summary>
    private void EvaluatePendingRules(
        LogScanConfig scan,
        IReadOnlyList<FileEntry> resolved,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tails,
        DateTime now,
        FileMonitoringState state,
        ILogger logger)
    {
        if (scan.PendingRules.Count == 0)
        {
            return;
        }

        var newest = NewestFile(resolved);
        var lines = newest == null ? Array.Empty<string>() : TailOf(tails, newest.FullPath);
        foreach (var rule in scan.PendingRules)
        {
            ApplyResult(state, LogPendingRunEvaluator.Evaluate(
                scan.Name, newest?.FullPath ?? scan.Path, lines, rule, now, newest?.LastWriteTime ?? now,
                scan.WikiUrl, scan.RemediationSteps, LogPendingRunEvaluator.DefaultAbandonedWindowHours, logger));
        }
    }

    /// <summary>Reads each file required by signature or run rules exactly once.</summary>
    private Dictionary<string, IReadOnlyList<string>> ReadFileTails(
        LogScanConfig scan,
        IReadOnlyList<FileEntry> signatureFiles,
        FileEntry? runFile)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var filesToRead = signatureFiles.ToList();
        if (runFile != null && filesToRead.All(f => f.FullPath != runFile.FullPath))
        {
            filesToRead.Add(runFile);
        }

        foreach (var file in filesToRead)
        {
            var isRunFile = runFile != null && file.FullPath == runFile.FullPath;
            var isSignatureFile = signatureFiles.Any(f => f.FullPath == file.FullPath);
            var maxLines = scan.MaxLines <= 0 && isSignatureFile
                ? 0
                : isRunFile ? PendingRuleLineBudget(scan) : scan.MaxLines;
            result[file.FullPath] = fileSystem.ReadLines(file.FullPath, maxLines);
        }

        return result;
    }

    /// <summary>Returns the tail of a file (empty when the file was not read).</summary>
    private static IReadOnlyList<string> TailOf(IReadOnlyDictionary<string, IReadOnlyList<string>> tails, string path)
        => tails.TryGetValue(path, out var lines) ? lines : Array.Empty<string>();

    internal static FileEntry? NewestFile(IEnumerable<FileEntry> files) => files
        .OrderByDescending(f => f.LastWriteTime)
        .ThenByDescending(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    /// <summary>Evaluates every expected-run rule against only the newest matching file.</summary>
    private void EvaluateExpectedRuns(
        LogScanConfig scan,
        IReadOnlyList<FileEntry> resolved,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tails,
        DateTime now,
        FileMonitoringState state,
        ILogger logger)
    {
        if (scan.ExpectedRuns.Count == 0)
        {
            return;
        }

        var newest = NewestFile(resolved);
        var lines = newest == null ? Array.Empty<string>() : TailOf(tails, newest.FullPath);
        foreach (var rule in scan.ExpectedRuns)
        {
            var result = ExpectedRunEvaluator.Evaluate(
                scan.Name, newest?.FullPath ?? scan.Path, lines, rule, now,
                scan.WikiUrl, scan.RemediationSteps, logger);

            if (result.EvaluationError != null)
            {
                logger.LogWarning(
                    "FileMonitoring: Log-Scan '{Scan}' – erwartete Läufe konnten nicht bewertet werden: {Error}",
                    scan.Name, result.EvaluationError);
                state.EvaluationErrors.Add($"{scan.Name}: {result.EvaluationError}");
            }

            foreach (var missing in result.Missing)
            {
                missing.LastRunAt = result.LastRun;
                state.MissingRuns.Add(missing);
            }
        }
    }

    /// <summary>
    /// Applies the generic age filter of the scan (only files that are not too old are scanned for
    /// error signatures).
    /// </summary>
    internal static IReadOnlyList<FileEntry> ApplyAgeFilter(
        IReadOnlyList<FileEntry> resolved,
        LogScanConfig scan,
        DateTime now)
    {
        var maxAge = scan.MaxFileAgeMinutes;
        if (maxAge is not > 0)
        {
            return resolved;
        }

        return resolved.Where(f => (now - f.LastWriteTime).TotalMinutes <= maxAge).ToList();
    }

    /// <summary>
    /// Line budget for the pending rules: at least the configured tail, capped so that a huge log
    /// cannot be read completely into memory.
    /// </summary>
    internal static int PendingRuleLineBudget(LogScanConfig scan)
    {
        const int min = 5000;
        const int max = 200_000;
        var configured = scan.MaxLines > 0 ? scan.MaxLines : min;
        return Math.Clamp(configured * 4, min, max);
    }

    private static void ApplyResult(FileMonitoringState state, PendingRunResult result)
    {
        state.PendingRuns.AddRange(result.Pending);
        state.RunningLogRuns.AddRange(result.Running);
        state.AbandonedRuns.AddRange(result.Abandoned);
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
