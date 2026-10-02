using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Moq;
using Metric = Minicon.SimpleAdmin.Models.Status.Metric;

namespace Minicon.SimpleAdmin.Tests.Services;

public class FileShareCheckerTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 22, 0, 0, DateTimeKind.Local);

    private static FileShareChecker Build(IFileSystemAccess fs) => new(fs, NullLogger<FileShareChecker>.Instance);

    // --- EvaluateDirectory (pure) ---

    [Fact]
    public void EvaluateDirectory_AgeOverCritical_YieldsCriticalAgeFinding()
    {
        var files = new[] { new FileEntry("/eingang/a.xml", "a.xml", Now.AddMinutes(-300)) };
        var dir = new WatchedDirectory { Name = "Eingang", Path = "/eingang" };

        var result = FileShareChecker.EvaluateDirectory(files, dir, new FileMonitoringDefaults(), Now).ToList();

        result.Should().ContainSingle();
        result[0].IsCritical.Should().BeTrue();
        result[0].Reason.Should().Be("age");
        result[0].Directory.Should().Be("Eingang");
    }

    [Fact]
    public void EvaluateDirectory_AgeBetweenWarnAndCrit_YieldsWarning()
    {
        var files = new[] { new FileEntry("/eingang/a.xml", "a.xml", Now.AddMinutes(-90)) };
        var dir = new WatchedDirectory { Name = "Eingang", Path = "/eingang" };

        var result = FileShareChecker.EvaluateDirectory(files, dir, new FileMonitoringDefaults(), Now).ToList();

        result.Should().ContainSingle();
        result[0].IsCritical.Should().BeFalse();
        result[0].Reason.Should().Be("age");
    }

    [Fact]
    public void EvaluateDirectory_CutoffActive_FlagsFreshFile()
    {
        // Fresh file (5 min) is not age-stuck, but it is present after the 21:45 cutoff (now = 22:00).
        var files = new[] { new FileEntry("/eingang/a.xml", "a.xml", Now.AddMinutes(-5)) };
        var dir = new WatchedDirectory { Name = "Eingang", Path = "/eingang", CutoffTime = "21:45", CutoffSeverity = "Critical" };

        var result = FileShareChecker.EvaluateDirectory(files, dir, new FileMonitoringDefaults(), Now).ToList();

        result.Should().ContainSingle();
        result[0].Reason.Should().Be("cutoff");
        result[0].IsCritical.Should().BeTrue();
    }

    [Fact]
    public void EvaluateDirectory_CutoffNotYetReached_IgnoresFreshFile()
    {
        var early = new DateTime(2026, 6, 22, 20, 0, 0, DateTimeKind.Local);
        var files = new[] { new FileEntry("/eingang/a.xml", "a.xml", early.AddMinutes(-5)) };
        var dir = new WatchedDirectory { Name = "Eingang", Path = "/eingang", CutoffTime = "21:45" };

        FileShareChecker.EvaluateDirectory(files, dir, new FileMonitoringDefaults(), early)
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateDirectory_IncludeExcludeFilters_Applied()
    {
        var files = new[]
        {
            new FileEntry("/eingang/a.xml", "a.xml", Now.AddMinutes(-300)),
            new FileEntry("/eingang/b.log", "b.log", Now.AddMinutes(-300)),
            new FileEntry("/eingang/c.tmp", "c.tmp", Now.AddMinutes(-300))
        };
        var dir = new WatchedDirectory
        {
            Name = "Eingang",
            Path = "/eingang",
            IncludePatterns = new() { "*.xml", "*.tmp" },
            ExcludePatterns = new() { "*.tmp" }
        };

        var result = FileShareChecker.EvaluateDirectory(files, dir, new FileMonitoringDefaults(), Now).ToList();

        result.Should().ContainSingle();
        result[0].FilePath.Should().EndWith("a.xml");
    }

    // --- EvaluateLogContent (pure) ---

    [Fact]
    public void EvaluateLogContent_AggregatesPerPattern_WithSample()
    {
        var lines = new[] { "alles gut", "Nachricht haengt fest", "ok", "msg stuck here" };
        var patterns = new List<LogPatternConfig>
        {
            new() { Name = "Hängende Nachricht", Severity = "Critical", Regex = "haengt|stuck" }
        };

        var result = FileShareChecker.EvaluateLogContent("Schnittstellen-Log", "/log/a.log", lines, patterns, null, new List<string>()).ToList();

        result.Should().ContainSingle();
        result[0].Count.Should().Be(2);
        result[0].Pattern.Should().Be("Hängende Nachricht");
        result[0].IsCritical.Should().BeTrue();
        result[0].SampleLine.Should().Be("Nachricht haengt fest");
    }

    [Fact]
    public void EvaluateLogContent_NoMatch_Empty()
    {
        var patterns = new List<LogPatternConfig> { new() { Name = "X", Regex = "fatal" } };
        FileShareChecker.EvaluateLogContent("L", "/log/a.log", new[] { "all fine" }, patterns, null, new List<string>())
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateLogContent_InvalidRegex_Skipped()
    {
        var patterns = new List<LogPatternConfig> { new() { Name = "Bad", Regex = "(unclosed" } };
        FileShareChecker.EvaluateLogContent("L", "/log/a.log", new[] { "(unclosed" }, patterns, null, new List<string>())
            .Should().BeEmpty();
    }

    [Fact]
    public void EvaluateLogContent_PatternWikiOverridesDefault()
    {
        var patterns = new List<LogPatternConfig>
        {
            new() { Name = "A", Regex = "x", WikiUrl = "https://wiki/a" },
            new() { Name = "B", Regex = "x" }
        };

        var result = FileShareChecker.EvaluateLogContent("L", "/log/a.log", new[] { "x" }, patterns, "https://wiki/default", new List<string>()).ToList();

        result.Single(m => m.Pattern == "A").WikiUrl.Should().Be("https://wiki/a");
        result.Single(m => m.Pattern == "B").WikiUrl.Should().Be("https://wiki/default");
    }

    // --- Check orchestration (mocked IFileSystemAccess) ---

    [Fact]
    public void Check_OutsideBusinessHours_SkipsAllChecks()
    {
        var fs = new Mock<IFileSystemAccess>(MockBehavior.Strict);
        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            Schedule = "Mon-Sun 06:00-08:00", // now = 22:00 → outside
            Directories = { new WatchedDirectory { Name = "Eingang", Path = "/eingang" } }
        };

        var state = Build(fs.Object).Check(config, new FileMonitoringDefaults(), Now);

        state.OutsideBusinessHours.Should().BeTrue();
        state.DirectoriesChecked.Should().Be(0);
        fs.VerifyNoOtherCalls();
    }

    [Fact]
    public void Check_ShareUnreachable_SetsAnyError()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new IOException("share offline"));
        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            Directories = { new WatchedDirectory { Name = "Eingang", Path = "\\\\srv\\eingang" } }
        };

        var state = Build(fs.Object).Check(config, new FileMonitoringDefaults(), Now);

        state.AnyError.Should().BeTrue();
        state.Error.Should().Contain("offline");
        state.StuckFiles.Should().BeEmpty();
    }

    [Fact]
    public void Check_FindsStuckFilesAndLogMatches()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.EnumerateFiles("/eingang", false))
            .Returns(new[] { new FileEntry("/eingang/old.xml", "old.xml", Now.AddMinutes(-300)) });
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Now) });
        fs.Setup(f => f.ReadLines("/log/a.log", It.IsAny<int>()))
            .Returns(new[] { "Nachricht haengt fest" });

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            Directories = { new WatchedDirectory { Name = "Eingang", Path = "/eingang", WikiUrl = "https://wiki/stuck" } },
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Schnittstellen-Log",
                    Path = "/log/a.log",
                    Patterns = { new LogPatternConfig { Name = "Hängend", Severity = "Critical", Regex = "haengt" } }
                }
            }
        };

        var state = Build(fs.Object).Check(config, new FileMonitoringDefaults(), Now);

        state.AnyError.Should().BeFalse();
        state.DirectoriesChecked.Should().Be(1);
        state.LogFilesScanned.Should().Be(1);
        state.StuckFiles.Should().ContainSingle().Which.WikiUrl.Should().Be("https://wiki/stuck");
        state.LogMatches.Should().ContainSingle().Which.Pattern.Should().Be("Hängend");
    }

    // --- ProblemDerivationService FileMonitoring branch ---

    [Fact]
    public void DeriveProblems_FileMonitoring_MapsStuckLogAndError()
    {
        var fm = new FileMonitoringState
        {
            AnyError = true,
            Error = "share offline",
            StuckFiles =
            {
                new StuckFile { Directory = "Eingang", FilePath = "/eingang/old.xml", AgeMinutes = 300, Reason = "age", IsCritical = true, WikiUrl = "https://wiki/x" }
            },
            LogMatches =
            {
                new LogMatch { LogScan = "Schnittstellen-Log", Pattern = "Hängend", Count = 3, IsCritical = false }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, null, null, null, fm);

        problems.Should().Contain(p => p.Id == "file_eingang_old_xml_stuck" && p.Severity == ProblemSeverity.Critical);
        problems.Single(p => p.Id == "file_eingang_old_xml_stuck").WikiUrl.Should().Be("https://wiki/x");
        problems.Should().Contain(p => p.Id == "log_schnittstellen_log_h_ngend_match" && p.Severity == ProblemSeverity.Warning);
        problems.Should().Contain(p => p.Id == "file_fileshare_unreachable" && p.Severity == ProblemSeverity.Critical);
    }

    // --- ActiveProblem factories ---

    [Fact]
    public void FileStuck_BuildsStableIdAndSeverity()
    {
        var p = ActiveProblem.FileStuck("Eingang", "/eingang/order.xml", 305, "age", isCritical: true,
            wikiUrl: "https://wiki/s", remediationSteps: new List<string> { "Schritt 1" });

        p.Id.Should().Be("file_eingang_order_xml_stuck");
        p.Severity.Should().Be(ProblemSeverity.Critical);
        p.WikiUrl.Should().Be("https://wiki/s");
        p.RemediationSteps.Should().ContainSingle().Which.Should().Be("Schritt 1");
    }

    [Fact]
    public void LogError_BuildsStableId()
    {
        var p = ActiveProblem.LogError("Schnittstellen-Log", "/log/a.log", "Hängende Nachricht", 4, "sample", isCritical: false);
        p.Id.Should().Be("log_schnittstellen_log_h_ngende_nachricht_match");
        p.Severity.Should().Be(ProblemSeverity.Warning);
    }

    [Fact]
    public void FileShareUnreachable_IsCritical()
    {
        var p = ActiveProblem.FileShareUnreachable("Kasse", "access denied");
        p.Id.Should().Be("file_kasse_unreachable");
        p.Severity.Should().Be(ProblemSeverity.Critical);
    }
}
