using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Regression tests for expected-run evidence windows, age-filter independence,
/// last-run reporting and regex timeouts.
/// </summary>
public class FileMonitoringNachbesserungTests
{
    // 2026-11-20 is a Friday, 2026-11-21 a Saturday, 2026-11-23 a Monday.
    private static readonly DateTime MondayMorning = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Local);
    private static readonly DateTime SaturdayMorning = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Local);

    private static FileShareChecker Build(IFileSystemAccess fs) => new(fs, NullLogger<FileShareChecker>.Instance);

    private static ExpectedRunRule Rule(string expectedTime = "22:00", int graceMinutes = 120) => new()
    {
        Name = "Abendlauf",
        MatchRegex = "Verarbeitungsergebnis:",
        Schedule = "Mon-Fri",
        ExpectedTime = expectedTime,
        GraceMinutes = graceMinutes,
        Severity = "Critical"
    };

    // ── K1: Belegfenster aus Fälligkeit und Grace ───────────────────────────

    [Fact]
    public void K1_LateRunWithinTheGrace_IsNotReportedAsMissing()
    {
        // The due slot is Friday 20.11. 22:00 with 120 min grace → the evidence is valid until
        // 00:00. The run happened at 23:30 — late, but within the grace period.
        var lines = new[]
        {
            "2026-11-20 23:30:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK"
        };

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().BeEmpty("the run happened inside the grace period");
        result.LastRun.Should().Be(new DateTime(2026, 11, 20, 23, 30, 0));
    }

    [Fact]
    public void K1_RunAfterTheGrace_IsReportedAsMissing()
    {
        // 01:30 is behind the end of the grace period (00:00) → the run counts as missing.
        var lines = new[]
        {
            "2026-11-21 01:30:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK"
        };

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().ContainSingle();
    }

    [Fact]
    public void K1_EvidenceWindowStart_IsDerivedFromTheGracePeriod()
    {
        // The lower bound of the evidence window is a fixed 12 h before the expected time (the
        // specification: "bzw. ab expectedTime − 12 h"). Only the UPPER bound follows the grace
        // period — a run that finishes within the grace period counts.
        var due = new DateTime(2026, 11, 20, 22, 0, 0, DateTimeKind.Local);

        ExpectedRunEvaluator.EvidenceWindowStart(due, 120).Should().Be(new DateTime(2026, 11, 20, 10, 0, 0));
        ExpectedRunEvaluator.EvidenceWindowStart(due, 30).Should().Be(new DateTime(2026, 11, 20, 10, 0, 0));
        ExpectedRunEvaluator.EvidenceWindowStart(due, 60 * 24).Should().Be(new DateTime(2026, 11, 20, 10, 0, 0));
    }

    // ── M1: letzter Lauf unabhängig vom Belegfenster ────────────────────────

    [Fact]
    public void M1_LastRunIsReportedEvenWhenTheExpectedRunIsMissing()
    {
        // The log contains an older successful run (13.11.) but not the due one (20.11.).
        var lines = new[]
        {
            "2026-11-13 22:05:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK"
        };

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.Missing.Should().ContainSingle();
        result.LastRun.Should().Be(new DateTime(2026, 11, 13, 22, 5, 0),
            "the last run must be visible even when the expected run is missing");
    }

    [Fact]
    public void M1_LastRunIsNotTakenFromTheFuture()
    {
        // A match after the end of the grace period is not a run of the due slot.
        var lines = new[]
        {
            "2026-11-21 10:00:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK"
        };

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, Rule(), SaturdayMorning);

        result.LastRun.Should().BeNull();
        result.Missing.Should().ContainSingle();
    }

    // ── M4: Regex-Timeout ist ein Auswertungsfehler ─────────────────────────

    [Fact]
    public void M4_RegexTimeout_ProducesNoMissingFindingButAnEvaluationError()
    {
        // A pathological expression: "^(a+)+$" against a long line of "a" hits the 500 ms timeout
        // (catastrophic backtracking). The evidence is then incomplete.
        var lines = new[] { new string('a', 60) + "b" };
        var rule = Rule();
        rule.MatchRegex = "^(a+)+$";

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, rule, SaturdayMorning);

        result.EvaluationError.Should().NotBeNull("a timeout means incomplete evidence");
        result.Missing.Should().BeEmpty("no missing finding may be derived from incomplete evidence");
    }

    [Fact]
    public void M4_RegexTimeout_DoesNotHideAMatchThatWasAlreadyFound()
    {
        // The first line is a valid proof of the run, the second line makes the expression time out.
        // The evaluation error is reported, but the run that was already proven is not turned into
        // a missing finding either.
        var lines = new[]
        {
            "2026-11-20 22:05:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK",
            new string('a', 60) + "b"
        };

        var rule = Rule();
        rule.MatchRegex = "^(a+)+$|Verarbeitungsergebnis:";

        var result = ExpectedRunEvaluator.Evaluate("Kassen-Log", "/log/kasse.log", lines, rule, SaturdayMorning);

        result.Missing.Should().BeEmpty("a timeout must not produce a missing finding");
    }

    // ── K3: Expected-Run-Belege unabhängig vom maxFileAgeMinutes-Filter ─────

    [Fact]
    public void K3_ExpectedRun_IsFoundEvenWhenTheFileIsOlderThanTheGenericAgeFilter()
    {
        // Monday 08:00: the Friday run (20.11. 22:05) must be found although the file was last
        // written on Friday and the scan filters files that are older than 2880 minutes (48 h).
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/kasse.log"))
            .Returns(new[] { new FileEntry("/log/kasse.log", "kasse.log", new DateTime(2026, 11, 20, 22, 5, 0, DateTimeKind.Local)) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { "2026-11-20 22:05:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK" });

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Kassen-Log",
                    Path = "/log/kasse.log",
                    MaxFileAgeMinutes = 2880,
                    ExpectedRuns = { Rule() }
                }
            }
        };

        var state = Build(fs.Object).Check(config, new FileMonitoringDefaults(), MondayMorning);

        state.MissingRuns.Should().BeEmpty("the Friday run is in the log — the age filter must not hide it");
    }

    [Fact]
    public void K3_SignatureScan_StillRespectsTheGenericAgeFilter()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/kasse.log"))
            .Returns(new[] { new FileEntry("/log/kasse.log", "kasse.log", new DateTime(2026, 11, 20, 22, 5, 0, DateTimeKind.Local)) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { "Nachricht haengt fest" });

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Kassen-Log",
                    Path = "/log/kasse.log",
                    MaxFileAgeMinutes = 2880,
                    Patterns = { new LogPatternConfig { Name = "Hängend", Severity = "Critical", Regex = "haengt" } }
                }
            }
        };

        var state = Build(fs.Object).Check(config, new FileMonitoringDefaults(), MondayMorning);

        state.LogMatches.Should().BeEmpty("the file is older than the generic age filter");
        state.LogFilesScanned.Should().Be(0);
    }

}
