using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Tests for the "open run" detection of the log monitoring (P7): a start marker in the log without
/// the matching end marker must be detected. The example log is the one from the requirement
/// (Michael, 26.11.2026).
/// </summary>
public class LogPendingRunEvaluatorTests
{
    // "Now" for the evaluation: 25.11.2026, 15:30 local time.
    // The last run of the example log started at 14:44:05 → 46 min open → over the 30 min limit.
    private static readonly DateTime Now = new(2026, 11, 25, 15, 30, 0, DateTimeKind.Local);

    /// <summary>"Now" shortly after the last start of the example log (14:44:05) → the run is still young.</summary>
    private static readonly DateTime NowSoon = new(2026, 11, 25, 15, 0, 0, DateTimeKind.Local);

    /// <summary>The example from the requirement, verbatim (CRLF, ~21 000 lines in the original).</summary>
    private static readonly string[] ExampleLog =
    {
        "2026-11-20 22:05:15.756 --- Starte Schnittstelle execute/schnittstelle/einlesen/KASSEN/JUKOS ---",
        "2026-11-20 22:05:15.756 open: http://localhost:8080/...",
        "2026-11-20 22:05:15.756 waiting for Result...",
        "2026-11-20 22:15:09.456 Schnittstelle Kassen/Jukos für Nutzer rshw verarbeitet. Verarbeitungsergebnis: OK",
        "2026-11-20 22:15:09.457",
        "Laufzeit: 00:09:53.699",
        "",
        "2026-11-25 13:40:41.040 --- Starte Schnittstelle ... ---",
        "2026-11-25 13:40:41.041 open: ...",
        "2026-11-25 13:40:41.041 waiting for Result...",
        "",
        "2026-11-25 14:14:06.933 --- Starte Schnittstelle ... ---",
        "...",
        "2026-11-25 14:44:05.679 waiting for Result..."
    };

    private static LogPendingRule Rule() => new()
    {
        Name = "Lauf muss abgeschlossen sein",
        StartRegex = "waiting for Result",
        EndRegex = "Verarbeitungsergebnis:",
        MaxMinutes = 30,
        Severity = "Critical"
    };

    private static PendingRunResult Evaluate(string[] lines, LogPendingRule? rule = null, DateTime? fileTime = null, DateTime? now = null)
        => LogPendingRunEvaluator.Evaluate(
            "Schnittstellen-Log", "/log/schnittstelle.log", lines, rule ?? Rule(), now ?? Now,
            fileTime ?? now ?? Now, null, null, LogPendingRunEvaluator.DefaultAbandonedWindowHours,
            NullLogger.Instance);

    // --- Beispiel aus der Anforderung ---

    [Fact]
    public void Example_LastRunOpenLongerThanMaxMinutes_IsReportedAsPending()
    {
        var result = Evaluate(ExampleLog);

        result.Pending.Should().ContainSingle();
        var pending = result.Pending[0];
        pending.Rule.Should().Be("Lauf muss abgeschlossen sein");
        pending.LogScan.Should().Be("Schnittstellen-Log");
        pending.IsCritical.Should().BeTrue();
        pending.StartedAt.Should().Be(new DateTime(2026, 11, 25, 14, 44, 5, 679, DateTimeKind.Local));
        pending.AgeMinutes.Should().BeApproximately(45.9, 0.2);
        pending.SampleLine.Should().Contain("waiting for Result");
    }

    [Fact]
    public void Example_RunningRunYoungerThanMaxMinutes_IsVisibleButNoProblem()
    {
        // 16 min < 30 min → the run is still going: visible in the state, but no problem.
        var result = Evaluate(ExampleLog, now: NowSoon);

        result.Pending.Should().BeEmpty();
        result.Running.Should().ContainSingle();
        result.Running[0].AgeMinutes.Should().BeApproximately(15.9, 0.2);
    }

    [Fact]
    public void Example_RunYoungerThanMaxMinutes_IsListedAsRunningWithoutProblem()
    {
        // The last run started 10 min ago → still running, no problem.
        var lines = new[]
        {
            "2026-11-25 14:50:00.000 --- Starte Schnittstelle ... ---",
            "2026-11-25 14:50:00.001 waiting for Result..."
        };

        var result = Evaluate(lines, now: new DateTime(2026, 11, 25, 15, 0, 0, DateTimeKind.Local));

        result.Pending.Should().BeEmpty();
        result.Running.Should().ContainSingle();
        result.Running[0].AgeMinutes.Should().BeApproximately(10, 0.1);
    }

    [Fact]
    public void Example_CompletedRunOf20November_ProducesNoFinding()
    {
        // The 20.11. run was closed by the result line → no pending, no abandoned.
        var lines = ExampleLog.Take(6).ToArray();

        var result = Evaluate(lines);

        result.Pending.Should().BeEmpty();
        result.Running.Should().BeEmpty();
        result.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void Example_ThreeAbandonedRuns_AreAggregated()
    {
        // Three starts without an end in between, then a new one → the first three are abandoned.
        var lines = new[]
        {
            "2026-11-25 10:00:00.000 waiting for Result...",
            "2026-11-25 11:00:00.000 waiting for Result...",
            "2026-11-25 12:00:00.000 waiting for Result...",
            "2026-11-25 13:00:00.000 waiting for Result...",
            "2026-11-25 13:30:00.000 Verarbeitungsergebnis: OK"
        };

        var result = Evaluate(lines);

        result.Abandoned.Should().ContainSingle();
        var abandoned = result.Abandoned[0];
        abandoned.Count.Should().Be(3);
        abandoned.LastSeenAt.Should().Be(new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Local));
        abandoned.SampleLine.Should().Contain("waiting for Result");
        result.Pending.Should().BeEmpty();
    }

    [Fact]
    public void Example_AbandonedRunsOutsideTheWindow_AreNotReported()
    {
        // The old leftovers (20.11.) are older than 24 h → they must not alarm forever.
        var lines = new[]
        {
            "2026-11-20 22:05:15.756 waiting for Result...",
            "2026-11-21 08:00:00.000 waiting for Result...",
            "2026-11-25 14:44:05.679 Verarbeitungsergebnis: OK"
        };

        var result = Evaluate(lines);

        result.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void ReportAbandoned_False_SuppressesTheAbandonedFinding()
    {
        var rule = Rule();
        rule.ReportAbandoned = false;
        var lines = new[]
        {
            "2026-11-25 13:00:00.000 waiting for Result...",
            "2026-11-25 13:30:00.000 waiting for Result...",
            "2026-11-25 14:30:00.000 Verarbeitungsergebnis: OK"
        };

        var result = Evaluate(lines, rule);

        result.Abandoned.Should().BeEmpty();
    }

    // --- Robustheit ---

    [Fact]
    public void ContinuationLines_WithoutTimestamp_DoNotBreakTheEvaluation()
    {
        var lines = new[]
        {
            "2026-11-25 13:40:41.040 --- Starte Schnittstelle ... ---",
            "2026-11-25 13:40:41.041 open: ...",
            "2026-11-25 13:40:41.041 waiting for Result...",
            "2026-11-25 13:40:41.042",
            "Laufzeit: 00:00:00.000"
        };

        var result = Evaluate(lines, now: new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Local));

        // The run is 20 min old → still running.
        result.Pending.Should().BeEmpty();
        result.Running.Should().ContainSingle();
        result.Running[0].StartedAt.Should().Be(new DateTime(2026, 11, 25, 13, 40, 41, 41, DateTimeKind.Local));
    }

    [Fact]
    public void MissingTimestamp_FallsBackToTheFileTime()
    {
        // No timestamp on the start line → the last-write time of the file is used.
        var fileTime = Now.AddMinutes(-90);
        var lines = new[] { "waiting for Result..." };

        var result = Evaluate(lines, fileTime: fileTime);

        result.Pending.Should().ContainSingle();
        result.Pending[0].StartedAt.Should().Be(fileTime);
        result.Pending[0].AgeMinutes.Should().BeApproximately(90, 0.1);
    }

    [Fact]
    public void CarriageReturns_AndEmptyLines_AreIgnored()
    {
        var lines = new[]
        {
            "2026-11-25 13:00:00.000 waiting for Result...\r",
            "\r",
            "",
            "2026-11-25 14:30:00.000 Verarbeitungsergebnis: OK\r"
        };

        var result = Evaluate(lines);

        result.Pending.Should().BeEmpty();
        result.Running.Should().BeEmpty();
        result.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void RotatedOrTruncatedLog_WithoutStartLine_ProducesNoFinding()
    {
        // The log was rotated: the tail no longer contains the start of the open run.
        var lines = new[]
        {
            "2026-11-25 13:40:41.041 open: ...",
            "2026-11-25 13:40:41.041 waiting for Result..."
        };

        // The start line IS present but the file is old → the run is 20 min old → running.
        var result = Evaluate(lines, now: new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Local));
        result.Pending.Should().BeEmpty();
        result.Running.Should().ContainSingle();

        // A tail without any start line → nothing at all.
        var empty = Evaluate(new[] { "2026-11-25 13:40:41.041 open: ..." });
        empty.Pending.Should().BeEmpty();
        empty.Running.Should().BeEmpty();
        empty.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void InvalidRegex_IsSkippedWithoutThrowing()
    {
        var rule = Rule();
        rule.StartRegex = "(unclosed";

        var result = Evaluate(ExampleLog, rule);

        result.Pending.Should().BeEmpty();
        result.Running.Should().BeEmpty();
        result.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void CatastrophicRegex_DoesNotHangTheEvaluation()
    {
        // A pathological expression must not block the worker (timeout like in the assistant).
        var rule = Rule();
        rule.StartRegex = "(a+)+$";
        var lines = Enumerable.Range(0, 200).Select(i => new string('a', 40) + "b").ToList();

        var result = Evaluate(lines.ToArray(), rule);

        // Either the timeout hit (no finding) or the rule did not match — but it must return.
        result.Pending.Should().BeEmpty();
    }

    [Fact]
    public void EmptyStartRegex_ProducesNoFinding()
    {
        var rule = Rule();
        rule.StartRegex = "";

        var result = Evaluate(ExampleLog, rule);

        result.Pending.Should().BeEmpty();
        result.Running.Should().BeEmpty();
        result.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public void AbortRegex_EndsTheRun_WithoutPendingFinding()
    {
        var rule = Rule();
        rule.AbortRegex = "Verarbeitung abgebrochen";
        var lines = new[]
        {
            "2026-11-25 13:40:41.041 waiting for Result...",
            "2026-11-25 13:45:00.000 Verarbeitung abgebrochen"
        };

        var result = Evaluate(lines, rule);

        result.Pending.Should().BeEmpty();
        result.Running.Should().BeEmpty();
    }

    [Fact]
    public void SeverityWarning_IsRespected()
    {
        var rule = Rule();
        rule.Severity = "Warning";

        var result = Evaluate(ExampleLog, rule);

        result.Pending.Should().ContainSingle();
        result.Pending[0].IsCritical.Should().BeFalse();
    }

    [Fact]
    public void WikiUrlAndRemediationSteps_FallBackToTheLogScanDefaults()
    {
        var rule = Rule();
        rule.WikiUrl = "https://wiki.intern/lauf";
        rule.RemediationSteps = new List<string> { "Job prüfen" };

        var withRule = Evaluate(ExampleLog, rule);
        withRule.Pending[0].WikiUrl.Should().Be("https://wiki.intern/lauf");
        withRule.Pending[0].RemediationSteps.Should().ContainSingle().Which.Should().Be("Job prüfen");

        var plainRule = Rule();
        var result = LogPendingRunEvaluator.Evaluate(
            "Schnittstellen-Log", "/log/a.log", ExampleLog, plainRule, Now, Now,
            "https://wiki.intern/default", new List<string> { "Schritt 1" },
            LogPendingRunEvaluator.DefaultAbandonedWindowHours, NullLogger.Instance);

        result.Pending[0].WikiUrl.Should().Be("https://wiki.intern/default");
        result.Pending[0].RemediationSteps.Should().ContainSingle().Which.Should().Be("Schritt 1");
    }

    [Fact]
    public void CustomTimestampFormatAndRegex_AreUsed()
    {
        var rule = Rule();
        rule.TimestampRegex = @"\[(\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})\]";
        rule.TimestampFormat = "dd.MM.yyyy HH:mm:ss";
        var lines = new[]
        {
            "[25.11.2026 13:40:41] waiting for Result..."
        };

        var result = Evaluate(lines, rule, now: new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Local));

        result.Running.Should().ContainSingle();
        result.Running[0].StartedAt.Should().Be(new DateTime(2026, 11, 25, 13, 40, 41, DateTimeKind.Local));
    }

    [Fact]
    public void TimestampWithoutMilliseconds_IsParsed()
    {
        var lines = new[] { "2026-11-25 13:40:41 waiting for Result..." };

        var result = Evaluate(lines, now: new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Local));

        result.Running.Should().ContainSingle();
        result.Running[0].StartedAt.Should().Be(new DateTime(2026, 11, 25, 13, 40, 41, DateTimeKind.Local));
    }

    [Fact]
    public void MaxMinutesZero_FallsBackToThirty()
    {
        var rule = Rule();
        rule.MaxMinutes = 0;
        var lines = new[] { "2026-11-25 13:40:41.000 waiting for Result..." }; // 20 min old

        var result = Evaluate(lines, rule, now: new DateTime(2026, 11, 25, 14, 0, 0, DateTimeKind.Local));

        result.Pending.Should().BeEmpty();
        result.Running.Should().ContainSingle();
    }
}
