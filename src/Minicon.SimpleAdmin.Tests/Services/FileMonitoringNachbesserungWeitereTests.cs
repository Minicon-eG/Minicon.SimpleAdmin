using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Helpers;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Weitere Regressionstests zu den Befunden des P7-Reviews, für die im ersten
/// Nachbesserungslauf noch kein eigener Test existierte:
/// <list type="bullet">
/// <item>M2 — WebUI-Badge und statisches HTML zählen die Befunde über denselben Zähler
/// (auch fehlende erwartete Läufe).</item>
/// <item>M3 — der Log-Tail wird mit begrenztem Ringpuffer gelesen.</item>
/// <item>M5 — der Trockenlauf rechnet mit dem tatsächlichen Dateialter, nicht mit „jetzt“.</item>
/// <item>M6 — eine Log-Walltime wird nicht als UTC umgedeutet.</item>
/// <item>G1 — auch die Liste der laufenden (noch nicht zu langen) Läufe wird gekappt.</item>
/// <item>G3 — der Standard der erwarteten Uhrzeit ist 22:00.</item>
/// </list>
/// </summary>
public class FileMonitoringNachbesserungWeitereTests
{
    // 2026-11-25 is a Wednesday.
    private static readonly DateTime Wednesday = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Local);

    // ── M2: gemeinsamer Zähler für WebUI-Badge und statisches HTML ──────────

    [Fact]
    public void M2_Counter_CountsEveryFindingKind()
    {
        var state = new FileMonitoringState
        {
            StuckFiles = { new StuckFile { IsCritical = true } },
            LogMatches = { new LogMatch { IsCritical = false } },
            PendingRuns = { new LogPendingRun { IsCritical = true } },
            AbandonedRuns = { new LogAbandonedRun { IsCritical = false } },
            MissingRuns = { new LogMissingRun { IsCritical = true } },
        };

        FileMonitoringFindingCounter.CountFindings(state).Should().Be(5);
        FileMonitoringFindingCounter.CountCritical(state).Should().Be(3);
    }

    [Fact]
    public void M2_Counter_OnlyAMissingExpectedRun_IsACriticalFinding()
    {
        // The finding that was missed in the static HTML badge: a missing expected run alone must
        // never be shown as "OK".
        var state = new FileMonitoringState
        {
            MissingRuns = { new LogMissingRun { IsCritical = true } }
        };

        FileMonitoringFindingCounter.CountFindings(state).Should().Be(1);
        FileMonitoringFindingCounter.CountCritical(state).Should().Be(1);
        FileMonitoringFindingCounter.Describe(state).Should().Contain("1 fehlende erwartete Läufe");
    }

    [Fact]
    public void M2_Counter_NullState_IsNeutral()
    {
        FileMonitoringFindingCounter.CountFindings(null).Should().Be(0);
        FileMonitoringFindingCounter.CountCritical(null).Should().Be(0);
        FileMonitoringFindingCounter.Describe(null).Should().BeEmpty();
    }

    [Fact]
    public void M2_Counter_AnErrorCountsAsOneCriticalFinding()
    {
        var state = new FileMonitoringState { AnyError = true, Error = "Freigabe nicht erreichbar" };

        FileMonitoringFindingCounter.CountCritical(state).Should().Be(1);
    }

    // ── M3: der Tail wird mit begrenztem Speicher gelesen ──────────────────────────────────

    [Fact]
    public void M3_ReadTail_ReturnsTheLastLines_OfALargeFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-tail-{Guid.NewGuid():N}.log");
        try
        {
            // 20 000 lines — a full read would be a noticeable amount of memory.
            var builder = new StringBuilder();
            for (var i = 0; i < 20_000; i++)
            {
                builder.Append("2026-11-25 10:00:00.000 Zeile ").Append(i).Append('\n');
            }

            File.WriteAllText(path, builder.ToString());

            var lines = new FileSystemAccess().ReadLines(path, 50);

            lines.Should().HaveCount(50);
            lines[^1].Should().Contain("Zeile 19999");
            lines[0].Should().Contain("Zeile 19950");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void M3_ReadTail_WithoutALimit_ReturnsTheWholeFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-whole-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "a\nb\nc\n");

            new FileSystemAccess().ReadLines(path, 0).Should().HaveCount(3);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void M3_PendingRuleLineBudget_IsBoundedAndGrowsWithMaxLines()
    {
        var scan = new LogScanConfig { MaxLines = 5000 };

        FileShareChecker.PendingRuleLineBudget(scan).Should().Be(20_000);

        // A very small tail is raised to the minimum of 5 000 lines (the pending rules need a
        // longer history than the signature scan).
        FileShareChecker.PendingRuleLineBudget(new LogScanConfig { MaxLines = 10 }).Should().Be(5000);

        // A huge tail is capped so that a big log cannot be read completely into memory.
        FileShareChecker.PendingRuleLineBudget(new LogScanConfig { MaxLines = 1_000_000 }).Should().Be(200_000);
    }

    // ── M5: der Trockenlauf rechnet mit dem echten Dateialter ───────────────

    [Fact]
    public async Task M5_Preview_UsesTheRealFileAge_AndNotNow()
    {
        var fs = new Mock<IFileSystemAccess>();
        // The file was written 90 minutes ago; the start line has no timestamp. The preview runs
        // against the real current time, so the age must be derived from the file time.
        var written = DateTime.Now.AddMinutes(-90);
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", written) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { "waiting for Result..." });

        var scan = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    MaxMinutes = 30,
                    Severity = "Critical"
                }
            }
        };

        var preview = await new FileMonitoringPreviewService(fs.Object).PreviewLogScanAsync(scan, null);

        preview.PendingRuns.Should().ContainSingle(
            "the run is 90 minutes old — with the file time as fallback it exceeds the 30 minutes");
        preview.PendingRuns[0].AgeMinutes.Should().BeApproximately(90, 5);
    }

    [Fact]
    public async Task M5_Preview_AStartLineWithATimestamp_UsesThatTimestamp()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", DateTime.Now.AddMinutes(-90)) });

        var startedAt = DateTime.Now.AddMinutes(-80);
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { startedAt.ToString("yyyy-MM-dd HH:mm:ss.fff") + " waiting for Result..." });

        var scan = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    MaxMinutes = 30,
                    Severity = "Critical"
                }
            }
        };

        var preview = await new FileMonitoringPreviewService(fs.Object).PreviewLogScanAsync(scan, null);

        preview.PendingRuns.Should().ContainSingle();
        preview.PendingRuns[0].StartedAt.Should().BeCloseTo(startedAt, TimeSpan.FromSeconds(1));
    }

    // ── M6: eine Log-Walltime wird nicht als UTC umgedeutet ──────────────────

    [Fact]
    public void M6_LogWallTime_IsNotShifted()
    {
        // The log line of "08:00" must be shown as "08:00" and not shifted by the offset between
        // the log server and the WebUI host.
        var wallTime = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Unspecified);

        Display.LogWallTime(wallTime).Should().Contain("08:00");
        Display.LogWallTimeSeconds(wallTime).Should().Contain("08:00:00");
    }

    [Fact]
    public void M6_LogWallTime_DiffersFromTheUtcInterpretation()
    {
        var wallTime = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Unspecified);

        // The UTC interpretation shifts the value (in CET by one hour) — the log wall time must not.
        Display.LogWallTime(wallTime).Should().NotBe(Display.LocalTime(wallTime));
    }

    // ── G1: auch die laufenden Läufe werden gekappt ─────────────────────────

    [Fact]
    public void G1_RunningLogRuns_AreCapped()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Wednesday) });

        // 60 start lines without an end → 60 open runs (all still within their allowed duration).
        var lines = Enumerable.Range(0, 60)
            .Select(i => $"2026-11-25 11:5{i % 10}:00.000 waiting for Result {i}")
            .ToArray();
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>())).Returns(lines);

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Schnittstellen-Log",
                    Path = "/log/a.log",
                    PendingRules =
                    {
                        new LogPendingRule
                        {
                            Name = "Lauf",
                            StartRegex = "waiting for Result",
                            EndRegex = "Verarbeitungsergebnis:",
                            MaxMinutes = 600,
                            Severity = "Critical"
                        }
                    }
                }
            }
        };

        var state = new FileShareChecker(fs.Object, NullLogger<FileShareChecker>.Instance)
            .Check(config, new FileMonitoringDefaults { MaxReportedItems = 5 }, Wednesday);

        state.RunningLogRuns.Count.Should().BeLessThanOrEqualTo(5,
            "the list of the running runs must also be limited");
    }

    // ── G3: der Standard der erwarteten Uhrzeit ─────────────────────────────

    [Fact]
    public void G3_ExpectedTime_HasTheDocumentedDefault()
    {
        new ExpectedRunRule().ExpectedTime.Should().Be("22:00");
        new ExpectedRunRule().GraceMinutes.Should().Be(120);
    }

    [Fact]
    public void G3_AnExpectedRuleWithoutATime_UsesTheDefault()
    {
        // A rule without an explicit time uses the default of 22:00 — the evaluation is not
        // silently skipped. The slot of the current day is due after 22:00 + 120 min grace, so the
        // evaluation runs at 00:30 of the next day.
        var rule = new ExpectedRunRule
        {
            Name = "Abendlauf",
            MatchRegex = "Verarbeitungsergebnis:",
            Schedule = "Mon-Fri"
        };

        var lines = new[] { "2026-11-25 22:05:00.000 Schnittstelle verarbeitet. Verarbeitungsergebnis: OK" };

        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", lines, rule, new DateTime(2026, 11, 26, 0, 30, 0, DateTimeKind.Local));

        result.Missing.Should().BeEmpty("the run of the default slot 22:00 is present");
        result.LastRun.Should().Be(new DateTime(2026, 11, 25, 22, 5, 0));
    }

    [Fact]
    public void G3_AnExpectedRuleWithoutATime_IsNotSkipped()
    {
        // Without the default the evaluation would be skipped silently — the missing run would
        // never be reported.
        var rule = new ExpectedRunRule
        {
            Name = "Abendlauf",
            MatchRegex = "Verarbeitungsergebnis:",
            Schedule = "Mon-Fri"
        };

        var result = ExpectedRunEvaluator.Evaluate(
            "Kassen-Log", "/log/kasse.log", Array.Empty<string>(), rule,
            new DateTime(2026, 11, 26, 0, 30, 0, DateTimeKind.Local));

        result.Missing.Should().ContainSingle();
        result.Missing[0].ExpectedAt.Should().Be(new DateTime(2026, 11, 25, 22, 0, 0));
    }
}
