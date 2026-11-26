using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Evaluates "open runs" in a log file: a start marker without the matching end marker
/// (e.g. "waiting for Result..." without a following "Verarbeitungsergebnis:").
/// <para>
/// The evaluation is pure and unit-testable: it works on the already-read lines, takes the current
/// time and the file time as injected parameters and never touches the file system itself.
/// </para>
/// </summary>
internal static class LogPendingRunEvaluator
{
    /// <summary>Maximum length of a sample line kept in the state (the rest is cut off).</summary>
    internal const int MaxSampleLength = 200;

    /// <summary>Default window (in hours) in which abandoned runs are reported — older ones are ignored.</summary>
    internal const int DefaultAbandonedWindowHours = 24;

    /// <summary>Timeout for a single regex match (a pathological pattern must not block the worker).</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Evaluates one pending rule against the read log lines.
    /// </summary>
    /// <param name="logScanName">Label of the log scan (used in messages and problem IDs).</param>
    /// <param name="logPath">Path of the log file (informational).</param>
    /// <param name="lines">The lines read from the tail of the log.</param>
    /// <param name="rule">The configured rule.</param>
    /// <param name="now">Current time (local time of the server running the check).</param>
    /// <param name="fileLastWrite">Last-write time of the log file — used when a start line has no timestamp.</param>
    /// <param name="defaultWikiUrl">Wiki link of the log scan (used when the rule has none).</param>
    /// <param name="defaultSteps">Remediation steps of the log scan (used when the rule has none).</param>
    /// <param name="abandonedWindowHours">
    /// Only abandoned runs within this window are reported (default 24 h) so that old leftovers
    /// do not alarm forever.
    /// </param>
    /// <param name="logger">Optional logger for invalid regexes.</param>
    internal static PendingRunResult Evaluate(
        string logScanName,
        string logPath,
        IReadOnlyList<string> lines,
        LogPendingRule rule,
        DateTime now,
        DateTime fileLastWrite,
        string? defaultWikiUrl = null,
        IReadOnlyList<string>? defaultSteps = null,
        int abandonedWindowHours = DefaultAbandonedWindowHours,
        ILogger? logger = null)
    {
        var result = new PendingRunResult();
        if (rule == null || string.IsNullOrWhiteSpace(rule.StartRegex))
        {
            return result;
        }

        var startRegex = TryCompile(rule.StartRegex, logger, logScanName);
        if (startRegex == null)
        {
            return result;
        }

        var endRegex = TryCompile(rule.EndRegex, logger, logScanName);
        var abortRegex = TryCompile(rule.AbortRegex, logger, logScanName);
        var timestampRegex = TryCompile(rule.TimestampRegex, logger, logScanName);
        var format = string.IsNullOrWhiteSpace(rule.TimestampFormat) ? "yyyy-MM-dd HH:mm:ss.fff" : rule.TimestampFormat;

        var wikiUrl = string.IsNullOrWhiteSpace(rule.WikiUrl) ? defaultWikiUrl : rule.WikiUrl;
        var steps = rule.RemediationSteps.Count > 0
            ? new List<string>(rule.RemediationSteps)
            : defaultSteps != null ? new List<string>(defaultSteps) : new List<string>();
        var isCritical = !string.Equals(rule.Severity, "Warning", StringComparison.OrdinalIgnoreCase);
        var maxMinutes = rule.MaxMinutes > 0 ? rule.MaxMinutes : 30;
        var abandonedCutoff = now.AddHours(-Math.Max(1, abandonedWindowHours));

        // One open run at a time: the current run (start line, timestamp) and the count of
        // abandoned runs (a newer start superseded an unfinished one).
        var openStart = (Line: (string?)null, At: (DateTime?)null);
        var abandonedCount = 0;
        DateTime? lastAbandonedAt = null;
        string lastAbandonedSample = string.Empty;

        foreach (var rawLine in lines)
        {
            var line = rawLine?.TrimEnd('\r') ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            bool matches;
            try
            {
                matches = startRegex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                logger?.LogWarning("FileMonitoring: Start-Ausdruck '{Pattern}' in Log-Scan '{Scan}' hat das Zeitlimit überschritten", rule.StartRegex, logScanName);
                return result;
            }

            if (!matches)
            {
                // A run can also be ended by a line that is not a start line.
                if (openStart.Line != null && Matches(endRegex, line, logger, logScanName))
                {
                    openStart = (null, null);
                }
                else if (openStart.Line != null && Matches(abortRegex, line, logger, logScanName))
                {
                    // An explicit abort ends the run — it is not reported as hanging.
                    openStart = (null, null);
                }

                continue;
            }

            // This line starts a new run.
            var startedAt = ParseTimestamp(line, timestampRegex, format);
            if (openStart.Line != null)
            {
                // The previous run was superseded without an end → abandoned (only recent ones count).
                var previousAt = openStart.At ?? (DateTime?)fileLastWrite;
                if (previousAt.HasValue && previousAt.Value >= abandonedCutoff)
                {
                    abandonedCount++;
                    lastAbandonedAt = previousAt;
                    lastAbandonedSample = openStart.Line;
                }
            }

            openStart = (Shorten(line), startedAt);
        }

        if (openStart.Line != null)
        {
            // The run is still open at the end of the file. Without a timestamp the file time is used.
            var startedAt = openStart.At ?? fileLastWrite;
            var ageMinutes = Math.Max(0, (now - startedAt).TotalMinutes);

            var entry = new LogPendingRun
            {
                LogScan = logScanName,
                LogPath = logPath,
                Rule = string.IsNullOrWhiteSpace(rule.Name) ? rule.StartRegex : rule.Name,
                StartedAt = startedAt,
                AgeMinutes = Math.Round(ageMinutes, 1),
                SampleLine = openStart.Line,
                IsCritical = isCritical,
                WikiUrl = wikiUrl,
                RemediationSteps = new List<string>(steps)
            };

            if (ageMinutes >= maxMinutes)
            {
                result.Pending.Add(entry);
            }
            else
            {
                result.Running.Add(entry);
            }
        }

        if (abandonedCount > 0 && rule.ReportAbandoned)
        {
            result.Abandoned.Add(new LogAbandonedRun
            {
                LogScan = logScanName,
                LogPath = logPath,
                Rule = string.IsNullOrWhiteSpace(rule.Name) ? rule.StartRegex : rule.Name,
                Count = abandonedCount,
                LastSeenAt = lastAbandonedAt,
                SampleLine = lastAbandonedSample,
                IsCritical = isCritical,
                WikiUrl = wikiUrl,
                RemediationSteps = new List<string>(steps)
            });
        }

        return result;
    }

    /// <summary>
    /// Parses the timestamp of a line: either via <paramref name="timestampRegex"/> (first capture
    /// group) or from the line start with <paramref name="format"/>. Returns null when no timestamp
    /// can be read (continuation lines such as "Laufzeit: …").
    /// </summary>
    internal static DateTime? ParseTimestamp(string line, Regex? timestampRegex, string format)
    {
        if (timestampRegex != null)
        {
            try
            {
                var m = timestampRegex.Match(line);
                if (m.Success)
                {
                    var group = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                    if (TryParseTimestamp(group, format, out var parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Fall through to the line-start parse.
            }
        }

        // Line start: "2026-11-20 22:05:15.756 waiting for Result…" — the timestamp may contain a
        // space itself ("yyyy-MM-dd HH:mm:ss.fff"), so the candidate is taken from the line start and
        // shortened until it parses.
        return ParseFromLineStart(line, format);
    }

    private static DateTime? ParseFromLineStart(string line, string format)
    {
        var max = Math.Min(line.Length, 40);
        for (var len = max; len > 0; len--)
        {
            var candidate = line[..len].TrimEnd();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (TryParseTimestamp(candidate, format, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryParseTimestamp(string value, string format, out DateTime result)
    {
        if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }

        // "yyyy-MM-dd HH:mm:ss" without milliseconds is common.
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }

        return false;
    }

    private static Regex? TryCompile(string? pattern, ILogger? logger, string logScanName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            logger?.LogWarning(ex, "FileMonitoring: ungültiges Regex '{Pattern}' in Log-Scan '{Scan}'", pattern, logScanName);
            return null;
        }
    }

    private static bool Matches(Regex? regex, string line, ILogger? logger, string logScanName)
    {
        if (regex == null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            logger?.LogWarning("FileMonitoring: Ausdruck in Log-Scan '{Scan}' hat das Zeitlimit überschritten", logScanName);
            return false;
        }
    }

    private static string Shorten(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxSampleLength ? trimmed : trimmed[..MaxSampleLength] + "…";
    }
}

/// <summary>Result of one pending-rule evaluation.</summary>
internal sealed class PendingRunResult
{
    /// <summary>Open runs that exceed their allowed duration → problem.</summary>
    public List<LogPendingRun> Pending { get; } = new();

    /// <summary>Open runs that are still within their allowed duration → visible, but no problem.</summary>
    public List<LogPendingRun> Running { get; } = new();

    /// <summary>Runs that were superseded by a newer start without a result.</summary>
    public List<LogAbandonedRun> Abandoned { get; } = new();
}
