using FluentAssertions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Tests for the "expected run" rule (P7, Nachtrag 26.11., Punkt 6): a run that should happen on
/// certain days (e.g. every working day at 22:00) but is missing from the log.
/// <para>
/// Calendar cases from the spec: Monday 08:00 → expects Friday; Saturday → expects Friday;
/// Friday's run missing → problem on Saturday; run present → nothing; grace not elapsed → nothing;
/// holiday in <c>holidays</c> → next previous working day.
/// </para>
/// </summary>
public class ExpectedRunEvaluatorTests
{
    // 2026-11-20 is a Friday, 2026-11-21 a Saturday, 2026-11-23 a Monday.
    private static readonly DateTime MondayMorning = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Local);
    private static readonly DateTime SaturdayMorning = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Local);
    private static readonly DateTime SaturdayEvening = new(2026, 11, 21, 23, 0, 0, DateTimeKind.Local);

    private static ExpectedRunRule Rule(
        string schedule = "Mon-Fri",
        string expectedTime = "22:00",
        int graceMinutes = 120,
        string? matchRegex = "Verarbeitungsergebnis:",
        IEnumerable<string>? holidays = null) => new()
        {
            Name = "Abendlauf",
            MatchRegex = matchRegex ?? "Verarbeitungsergebnis:",
            Schedule = schedule,
            ExpectedTime = expectedTime,
            GraceMinutes = graceMinutes,
            Severity = "Critical",
            Holidays = holidays?.ToList() ?? new List<string>()
        };

    private static string[] LogWithRunOn(params DateTime[] timestamps)
        => timestamps
            .Select(t => $"{t:yyyy-MM-dd HH:mm:ss.fff} Schnittstelle verarbeitet. Verarbeitungsergebnis: OK")
            .ToArray();

    // ── Der letzte fällige Termin ─────────────────────────────────────────────

    [Fact]
    public void MondayMorning_ExpectsThePreviousFriday()
    {
        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "Mon-Fri", new TimeSpan(22, 0, 0), 120, new HashSet<DateTime>(), MondayMorning);

        due.Should().NotBeNull();
        due!.Value.Should().Be(new DateTime(2026, 11, 20, 22, 0, 0));
        due.Value.DayOfWeek.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public void SaturdayMorning_AlsoExpectsFriday()
    {
        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "Mon-Fri", new TimeSpan(22, 0, 0), 120, new HashSet<DateTime>(), SaturdayMorning);

        due.Should().NotBeNull();
        due!.Value.Should().Be(new DateTime(2026, 11, 20, 22, 0, 0));
    }

    [Fact]
    public void GraceNotElapsedYet_TheCurrentDayIsNotDueYet()
    {
        // 22:00 + 120 min grace = 00:00 — at 23:00 on the due day the slot of that day is not
        // due yet, so the previous working day (Thursday, 19.11.) is expected.
        var now = new DateTime(2026, 11, 20, 23, 0, 0, DateTimeKind.Local);

        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "Mon-Fri", new TimeSpan(22, 0, 0), 120, new HashSet<DateTime>(), now);

        due.Should().NotBeNull();
        due!.Value.DayOfWeek.Should().Be(DayOfWeek.Thursday);
        due.Value.Should().Be(new DateTime(2026, 11, 19, 22, 0, 0));
    }

    [Fact]
    public void Holiday_IsSkippedAndThePreviousWorkingDayIsExpected()
    {
        var holidays = new HashSet<DateTime> { new(2026, 11, 20) };

        // Monday 23.11.: Friday 20.11. is a holiday → Thursday 19.11. is expected.
        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "Mon-Fri", new TimeSpan(22, 0, 0), 120, holidays, MondayMorning);

        due.Should().NotBeNull();
        due!.Value.Should().Be(new DateTime(2026, 11, 19, 22, 0, 0));
    }

    [Theory]
    [InlineData("Mon-Fri", DayOfWeek.Monday, true)]
    [InlineData("Mon-Fri", DayOfWeek.Friday, true)]
    [InlineData("Mon-Fri", DayOfWeek.Saturday, false)]
    [InlineData("Mon-Fri", DayOfWeek.Sunday, false)]
    [InlineData("Mon,Wed,Fri", DayOfWeek.Wednesday, true)]
    [InlineData("Mon,Wed,Fri", DayOfWeek.Tuesday, false)]
    [InlineData("Sat", DayOfWeek.Saturday, true)]
    [InlineData("", DayOfWeek.Sunday, true)]
    public void ScheduleDayMatching_WorksForTheDaySyntax(string schedule, DayOfWeek day, bool expected)
    {
        ExpectedRunEvaluator.IsExpectedDay(schedule, day).Should().Be(expected);
    }

    [Fact]
    public void ScheduleWithTimeRange_IgnoresTheTimePart()
    {
        // "Mon-Fri 06:00-22:00" — the time part must not influence the day check.
        ExpectedRunEvaluator.IsExpectedDay("Mon-Fri 06:00-22:00", DayOfWeek.Friday).Should().BeTrue();
        ExpectedRunEvaluator.IsExpectedDay("Mon-Fri 06:00-22:00", DayOfWeek.Saturday).Should().BeFalse();
    }

    // ── Fehlende Läufe ───────────────────────────────────────────────────────

    [Fact]
    public void MissingFridayRun_IsReportedOnSaturday()
    {
        // The log contains only the run of the previous week — Friday's run is missing.
        var lines = LogWithRunOn(new DateTime(2026, 11, 13, 22, 5, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().ContainSingle();
        result.Missing[0].LogScan.Should().Be("Kassen-Log");
        result.Missing[0].ExpectedAt.Should().Be(new DateTime(2026, 11, 20, 22, 0, 0));
        result.Missing[0].IsCritical.Should().BeTrue();
    }

    [Fact]
    public void PresentRun_IsNotReported()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 20, 22, 5, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().BeEmpty();
        result.LastRun.Should().Be(new DateTime(2026, 11, 20, 22, 5, 0));
    }

    [Fact]
    public void GraceNotElapsed_MissingRunIsNotReported()
    {
        // The slot of the current day is only due after the grace period (22:00 + 120 min = 00:00).
        // At 23:59 the slot of the 20.11. is not due yet → the slot of the 19.11. is expected and
        // that one is present in the log → nothing is reported.
        var now = new DateTime(2026, 11, 20, 23, 59, 0, DateTimeKind.Local);
        var lines = LogWithRunOn(new DateTime(2026, 11, 19, 22, 5, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), now);

        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void RunFromThePreviousDay_DoesNotCountForTheExpectedDay()
    {
        // The match window starts 12 h before the expected time → a run from the previous
        // evening (22:05 the day before) is outside it.
        var lines = LogWithRunOn(new DateTime(2026, 11, 19, 22, 5, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().ContainSingle();
    }

    [Fact]
    public void RunInTheAfternoonOfTheExpectedDay_Counts()
    {
        // The window starts 12 h before the expected time (the specification: a proof counts from
        // "expectedTime − 12 h"), so a run at 10:30 proves the 22:00 run of the same day → it is
        // NOT reported as missing.
        var lines = LogWithRunOn(new DateTime(2026, 11, 20, 10, 30, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().BeEmpty("a run within the 12 h before the expected time proves the run");
    }

    [Fact]
    public void RunInTheAfternoonOfTheExpectedDay_Counts_WithAWideGracePeriod()
    {
        // With a grace period of 24 h the window reaches 12 h back (the cap), so the run at 10:30
        // is inside the evidence window and the expected run is proven.
        var lines = LogWithRunOn(new DateTime(2026, 11, 20, 10, 30, 0));
        var rule = Rule();
        rule.GraceMinutes = 24 * 60;

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, rule, SaturdayMorning);

        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void RunBeforeTheEvidenceWindow_DoesNotCount()
    {
        // The window starts 12 h before the expected time → a run at 06:00 is outside it and does
        // not prove the 22:00 run.
        var lines = LogWithRunOn(new DateTime(2026, 11, 20, 6, 0, 0));

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().ContainSingle("a run before expectedTime − 12 h does not count");
    }

    [Fact]
    public void HolidayOnTheExpectedDay_NoReport()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 13, 22, 5, 0));
        var rule = Rule(holidays: new[] { "2026-11-20" });

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, rule, SaturdayMorning);

        // Friday is a holiday → Thursday 19.11. is expected and also missing.
        result.Missing.Should().ContainSingle();
        result.Missing[0].ExpectedAt.Should().Be(new DateTime(2026, 11, 19, 22, 0, 0));
    }

    [Fact]
    public void MidnightWindow_IncludesPreviousDayNoonAndEndsAtGrace()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 19, 12, 30, 0));
        var rule = Rule(expectedTime: "00:00");

        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", lines, rule,
            new DateTime(2026, 11, 21, 0, 30, 0, DateTimeKind.Local));

        result.Missing.Should().BeEmpty();
        result.LastRun.Should().Be(new DateTime(2026, 11, 19, 12, 30, 0));
    }

    [Fact]
    public void LateRunBeforeNow_IsReportedAsLastRun()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 21, 10, 0, 0));

        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", lines, Rule(),
            new DateTime(2026, 11, 21, 11, 0, 0, DateTimeKind.Local));

        result.Missing.Should().ContainSingle();
        result.LastRun.Should().Be(new DateTime(2026, 11, 21, 10, 0, 0));
    }

    [Fact]
    public void LongGrace_ChecksEveryCandidateBeforeCallingItDue()
    {
        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "Mon-Fri", new TimeSpan(22, 0, 0), 48 * 60, new HashSet<DateTime>(),
            new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Local));

        due.Should().Be(new DateTime(2026, 11, 20, 22, 0, 0));
    }

    [Fact]
    public void WarningSeverity_IsCarried()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 13, 22, 5, 0));
        var rule = Rule();
        rule.Severity = "Warning";

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, rule, SaturdayMorning);

        result.Missing.Should().ContainSingle();
        result.Missing[0].IsCritical.Should().BeFalse();
    }

    [Fact]
    public void WikiAndStepsFallBackToTheLogScanDefaults()
    {
        var lines = LogWithRunOn(new DateTime(2026, 11, 13, 22, 5, 0));

        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning,
            "https://wiki.intern/lauf", new[] { "Job prüfen" });

        result.Missing.Should().ContainSingle();
        result.Missing[0].WikiUrl.Should().Be("https://wiki.intern/lauf");
        result.Missing[0].RemediationSteps.Should().Equal("Job prüfen");
    }

    [Fact]
    public void InvalidRegex_IsIgnoredWithoutThrowing()
    {
        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", Array.Empty<string>(),
            Rule(matchRegex: "([unclosed"), SaturdayMorning);

        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void InvalidExpectedTime_IsIgnoredWithoutThrowing()
    {
        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", Array.Empty<string>(),
            Rule(expectedTime: "keine Uhrzeit"), SaturdayMorning);

        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void EmptySchedule_MeansEveryDay()
    {
        // Saturday 23:00 — the slot of the Saturday itself (22:00 + 120 min grace = 00:00) is not
        // due yet, so the Friday is expected. Without a schedule every day counts.
        var due = ExpectedRunEvaluator.FindLastDueSlot(
            "", new TimeSpan(22, 0, 0), 120, new HashSet<DateTime>(), SaturdayEvening);

        due.Should().NotBeNull();
        due!.Value.Should().Be(new DateTime(2026, 11, 20, 22, 0, 0));

        // On Sunday 01:00 the Saturday slot is due (grace elapsed).
        var dueSunday = ExpectedRunEvaluator.FindLastDueSlot(
            "", new TimeSpan(22, 0, 0), 120, new HashSet<DateTime>(),
            new DateTime(2026, 11, 22, 1, 0, 0, DateTimeKind.Local));

        dueSunday.Should().NotBeNull();
        dueSunday!.Value.Should().Be(new DateTime(2026, 11, 21, 22, 0, 0));
    }

    [Fact]
    public void HolidayList_IgnoresUnreadableEntries()
    {
        var holidays = ExpectedRunEvaluator.ParseHolidays(new[] { "2026-11-20", "kein Datum", "", "2026-12-24" });

        holidays.Should().HaveCount(2);
        holidays.Should().Contain(new DateTime(2026, 11, 20));
        holidays.Should().Contain(new DateTime(2026, 12, 24));
    }

    // ── Problem-Anbindung ────────────────────────────────────────────────────

    [Fact]
    public void MissingRun_ProducesAStableProblemId()
    {
        var problem = ActiveProblem.LogRunMissing(
            "Kassen-Log", "/log/kasse.log", "Abendlauf", new DateTime(2026, 11, 20, 22, 0, 0), null, true);

        problem.Id.Should().Be("log_kassen_log_abendlauf_missing");
        problem.Type.Should().Be("log");
        problem.Source.Should().Be("FileMonitoring");
        problem.Severity.Should().Be(ProblemSeverity.Critical);
        problem.Message.Should().Contain("fehlt");
        problem.Message.Should().Contain("20.11.2026");
        problem.Message.Should().Contain("Abendlauf");
    }
}
