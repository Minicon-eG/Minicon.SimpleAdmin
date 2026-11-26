using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Evaluates rules for <em>erwartete Läufe</em>: a run that should happen on certain days
/// (e.g. every working day at 22:00) but is missing from the log.
/// <para>
/// The evaluation is pure and unit-testable: it works on the already-read lines, takes the current
/// time as an injected parameter and never touches the file system itself.
/// </para>
/// </summary>
internal static class ExpectedRunEvaluator
{
    /// <summary>Timeout for a single regex match (a pathological pattern must not block the worker).</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How far back a matching line may be and still count for the expected day. The lower bound of
    /// the evidence window is a fixed <see cref="MatchWindowHoursBefore"/> hours before the expected
    /// time of the due slot (so that a run of the previous day never counts), the upper bound is the
    /// end of the grace period of the due slot.
    /// <para>
    /// The lower bound must <em>not</em> be derived from the grace period: a grace period of
    /// e.g. 120 min would narrow the window to 20:00–00:00 and a spec-conforming run at 10:30
    /// would be reported as missing although the spec allows a proof from
    /// <c>expectedTime − 12 h</c>. Only the upper bound follows the grace period — a run that
    /// starts late (after the expected time) still counts as long as it finishes within the grace
    /// period.
    /// </para>
    /// </summary>
    internal const int MatchWindowHoursBefore = 12;

    /// <summary>
    /// Evaluates one expected-run rule against the read log lines.
    /// </summary>
    /// <param name="logScanName">Label of the log scan (used in messages and problem IDs).</param>
    /// <param name="logPath">Path of the log file (informational).</param>
    /// <param name="lines">The lines read from the tail of the log.</param>
    /// <param name="rule">The configured rule.</param>
    /// <param name="now">Current time (local time of the server running the check).</param>
    /// <param name="defaultWikiUrl">Wiki link of the log scan (used when the rule has none).</param>
    /// <param name="defaultSteps">Remediation steps of the log scan (used when the rule has none).</param>
    /// <param name="logger">Optional logger for invalid regexes.</param>
    internal static ExpectedRunResult Evaluate(
        string logScanName,
        string logPath,
        IReadOnlyList<string> lines,
        ExpectedRunRule rule,
        DateTime now,
        string? defaultWikiUrl = null,
        IReadOnlyList<string>? defaultSteps = null,
        ILogger? logger = null)
    {
        var result = new ExpectedRunResult();
        if (rule == null || string.IsNullOrWhiteSpace(rule.MatchRegex))
        {
            return result;
        }

        var matchRegex = TryCompile(rule.MatchRegex, logger, logScanName);
        if (matchRegex == null)
        {
            return result;
        }

        if (!TryParseExpectedTime(rule.ExpectedTime, out var expectedTime))
        {
            logger?.LogWarning(
                "FileMonitoring: Regel '{Rule}' in Log-Scan '{Scan}' hat keine gültige Uhrzeit ('{Time}')",
                rule.Name, logScanName, rule.ExpectedTime);
            return result;
        }

        var grace = rule.GraceMinutes > 0 ? rule.GraceMinutes : 120;
        var holidays = ParseHolidays(rule.Holidays);

        // The last due slot: the most recent day on which a run was expected and whose grace
        // period has elapsed. On Monday morning that is the previous Friday.
        var due = FindLastDueSlot(rule.Schedule, expectedTime, grace, holidays, now);
        if (due == null)
        {
            // Nothing is due yet (e.g. the grace period has not elapsed) → no finding.
            return result;
        }

        var dueDate = due.Value;
        var windowStart = EvidenceWindowStart(dueDate, grace);
        var windowEnd = dueDate.AddMinutes(grace);

        // Two independent questions:
        //  * The informative "last run" is the newest matching line that is not newer than *now*.
        //    It must also be available when the run of the due slot is missing (e.g. a run that was
        //    made late, after the grace period, is still the last run).
        //  * The proof of the due slot is a matching line inside the evidence window
        //    (windowStart … windowEnd).
        var lastMatch = FindLastMatch(lines, matchRegex, DateTime.MinValue, now, logger, logScanName, result);
        result.LastRun = lastMatch;

        var evidenceMatch = FindLastMatch(lines, matchRegex, windowStart, windowEnd, logger, logScanName);
        if (evidenceMatch.HasValue)
        {
            // The run happened inside the evidence window → nothing to report.
            return result;
        }

        if (result.EvaluationError != null)
        {
            // Incomplete evidence (e.g. a regex timeout): no missing finding is derived from it.
            return result;
        }

        var wikiUrl = string.IsNullOrWhiteSpace(rule.WikiUrl) ? defaultWikiUrl : rule.WikiUrl;
        var steps = rule.RemediationSteps.Count > 0
            ? new List<string>(rule.RemediationSteps)
            : defaultSteps != null ? new List<string>(defaultSteps) : new List<string>();
        var isCritical = !string.Equals(rule.Severity, "Warning", StringComparison.OrdinalIgnoreCase);

        result.Missing.Add(new LogMissingRun
        {
            LogScan = logScanName,
            LogPath = logPath,
            Rule = string.IsNullOrWhiteSpace(rule.Name) ? rule.MatchRegex : rule.Name,
            ExpectedAt = dueDate,
            MatchRegex = rule.MatchRegex,
            IsCritical = isCritical,
            WikiUrl = wikiUrl,
            RemediationSteps = new List<string>(steps)
        });

        return result;
    }

    /// <summary>
    /// Finds the last due slot: the most recent day (before or equal to <paramref name="now"/>) on
    /// which a run was expected and whose grace period has elapsed. Holidays are skipped.
    /// </summary>
    internal static DateTime? FindLastDueSlot(
        string schedule,
        TimeSpan expectedTime,
        int graceMinutes,
        IReadOnlyCollection<DateTime> holidays,
        DateTime now)
    {
        // A run of the current day is only due after the grace period has elapsed.
        var start = now.Date;

        // At most 14 days back are searched — a schedule gap longer than that is a configuration error.
        for (var back = 0; back <= 14; back++)
        {
            var day = start.AddDays(-back);
            if (!IsExpectedDay(schedule, day.DayOfWeek))
            {
                continue;
            }

            if (holidays.Contains(day.Date))
            {
                continue;
            }

            var due = day.Add(expectedTime);

            // Every candidate — not only the current day — is only due when its grace period has
            // elapsed. With a 48 h grace period, Monday 22:00 is not yet due on Tuesday 08:00; the
            // search continues with the previous scheduled day (Friday).
            if (now < due.AddMinutes(graceMinutes))
            {
                continue;
            }

            return due;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a weekday is part of the rule schedule. The schedule uses the day syntax of
    /// the <see cref="TimeProfile"/> configuration, e.g. "Mon-Fri", "Mon,Wed,Fri" or "Sat".
    /// An empty schedule means every day.
    /// </summary>
    internal static bool IsExpectedDay(string schedule, DayOfWeek dayOfWeek)
    {
        if (string.IsNullOrWhiteSpace(schedule))
        {
            return true;
        }

        // Only the day part matters — a time range in the schedule is ignored.
        var dayPart = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dayPart))
        {
            return true;
        }

        foreach (var part in dayPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var range = part.Split('-', StringSplitOptions.TrimEntries);
                if (range.Length == 2 &&
                    TryParseDay(range[0], out var startDay) &&
                    TryParseDay(range[1], out var endDay))
                {
                    // Wrap-around ranges (e.g. "Sat-Mon") are supported.
                    var inRange = startDay <= endDay
                        ? dayOfWeek >= startDay && dayOfWeek <= endDay
                        : dayOfWeek >= startDay || dayOfWeek <= endDay;
                    if (inRange)
                    {
                        return true;
                    }
                }

                continue;
            }

            if (TryParseDay(part, out var singleDay) && singleDay == dayOfWeek)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseDay(string value, out DayOfWeek day)
    {
        var normalized = value.Trim();
        if (ShortDayNames.TryGetValue(normalized, out var shortName))
        {
            day = shortName;
            return true;
        }

        if (Enum.TryParse(normalized, ignoreCase: true, out day))
        {
            return Enum.IsDefined(day);
        }

        day = default;
        return false;
    }

    /// <summary>The short day names used by the schedule syntax ("Mon", "Tue", …).</summary>
    private static readonly Dictionary<string, DayOfWeek> ShortDayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday
    };

    /// <summary>Parses the holiday list (ISO dates). Unreadable entries are ignored.</summary>
    internal static HashSet<DateTime> ParseHolidays(IEnumerable<string>? holidays)
    {
        var result = new HashSet<DateTime>();
        if (holidays == null)
        {
            return result;
        }

        foreach (var entry in holidays)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    entry.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                result.Add(date.Date);
            }
        }

        return result;
    }

    /// <summary>Parses the expected time ("22:00" or "22:00:00").</summary>
    internal static bool TryParseExpectedTime(string? value, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        if (DateTime.TryParseExact(
                trimmed,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            result = time.TimeOfDay;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lower bound of the evidence window of the due slot: a fixed
    /// <see cref="MatchWindowHoursBefore"/> hours before the expected time of the due slot.
    /// <para>
    /// The lower bound is deliberately independent of the grace period — the specification allows a
    /// proof from <c>expectedTime − 12 h</c>. Only the <em>upper</em> bound is derived from the
    /// grace period of the due slot (see <see cref="Evaluate"/>), so a run that finishes within the
    /// grace period counts.
    /// </para>
    /// </summary>
    internal static DateTime EvidenceWindowStart(DateTime dueDate, int graceMinutes)
        => dueDate.AddHours(-MatchWindowHoursBefore);

    /// <summary>
    /// Finds the most recent line matching the rule inside the window of the expected day.
    /// The timestamp of a line is read from the line start ("yyyy-MM-dd HH:mm:ss.fff").
    /// </summary>
    private static DateTime? FindLastMatch(
        IReadOnlyList<string> lines,
        Regex matchRegex,
        DateTime windowStart,
        DateTime windowEnd,
        ILogger? logger,
        string logScanName,
        ExpectedRunResult? result = null)
    {
        DateTime? last = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine?.TrimEnd('\r') ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            bool matched;
            try
            {
                matched = matchRegex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                logger?.LogWarning(
                    "FileMonitoring: Ausdruck '{Pattern}' in Log-Scan '{Scan}' hat das Zeitlimit überschritten",
                    matchRegex.ToString(), logScanName);

                // A timeout means incomplete evidence — the caller must not derive a missing finding
                // from it. The error is reported in the state so that it stays visible.
                if (result != null)
                {
                    result.EvaluationError =
                        $"Ausdruck '{matchRegex}' hat das Zeitlimit überschritten — keine Bewertung möglich.";
                }

                return null;
            }

            if (!matched)
            {
                continue;
            }

            // The timestamp of the line — without it the line cannot be assigned to a day.
            var stamp = LogPendingRunEvaluator.ParseTimestamp(line, null, "yyyy-MM-dd HH:mm:ss.fff");
            if (stamp == null)
            {
                continue;
            }

            if (stamp.Value < windowStart || stamp.Value > windowEnd)
            {
                continue;
            }

            if (!last.HasValue || stamp.Value > last.Value)
            {
                last = stamp.Value;
            }
        }

        return last;
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
}

/// <summary>Result of one expected-run evaluation.</summary>
internal sealed class ExpectedRunResult
{
    /// <summary>Expected runs that are missing from the log.</summary>
    public List<LogMissingRun> Missing { get; } = new();

    /// <summary>
    /// Timestamp of the last matching run that was not newer than the evaluation time (null = none).
    /// It is independent of the evidence window of the due slot and therefore also available when
    /// the expected run is missing, including a late run after the grace period.
    /// </summary>
    public DateTime? LastRun { get; set; }

    /// <summary>
    /// Set when the evaluation could not be completed (e.g. the regex hit its time limit). The
    /// evidence is then incomplete and no missing finding is derived from it.
    /// </summary>
    public string? EvaluationError { get; set; }
}
