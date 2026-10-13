using FluentAssertions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class FileMonitoringPreviewServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 22, 0, 0, DateTimeKind.Local);

    // --- Directory ---

    [Fact]
    public void BuildDirectoryPreview_CountsFilteredFilesExtensionsAndFlagsLikeTheChecker()
    {
        var files = new[]
        {
            new FileEntry("/in/a.xml", "a.xml", Now.AddMinutes(-300)),
            new FileEntry("/in/b.xml", "b.xml", Now.AddMinutes(-5)),
            new FileEntry("/in/c.tmp", "c.tmp", Now.AddMinutes(-500)),
        };
        var dir = new WatchedDirectory { Name = "Eingang", Path = "/in", ExcludePatterns = { "*.tmp" } };

        var result = FileMonitoringPreviewService.BuildDirectoryPreview(files, dir, new FileMonitoringDefaults(), Now);

        result.Reachable.Should().BeTrue();
        result.TotalFiles.Should().Be(3);
        result.MatchingFiles.Should().Be(2);
        result.Extensions.Should().BeEquivalentTo(new[] { new ExtensionCount(".xml", 2), new ExtensionCount(".tmp", 1) });
        result.FlaggedCount.Should().Be(1);
        result.Flagged.Single().FilePath.Should().Be("/in/a.xml");
        result.OldestFiles.First().Path.Should().Be("/in/a.xml");
        result.WarningMinutes.Should().Be(60);
        result.CriticalMinutes.Should().Be(240);
    }

    [Fact]
    public void BuildDirectoryPreview_ReportsActiveCutoff()
    {
        var dir = new WatchedDirectory { Path = "/in", CutoffTime = "21:45" };

        var result = FileMonitoringPreviewService.BuildDirectoryPreview(
            new[] { new FileEntry("/in/a.xml", "a.xml", Now.AddMinutes(-1)) }, dir, new FileMonitoringDefaults(), Now);

        result.CutoffActiveNow.Should().BeTrue();
        result.Flagged.Single().Reason.Should().Be("cutoff");
    }

    [Fact]
    public async Task PreviewDirectoryAsync_UnreachablePath_ReturnsError()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), It.IsAny<bool>()))
          .Throws(new DirectoryNotFoundException("Pfad nicht gefunden"));

        var result = await new FileMonitoringPreviewService(fs.Object)
            .PreviewDirectoryAsync(new WatchedDirectory { Path = @"\\srv\weg" }, new FileMonitoringDefaults());

        result.Reachable.Should().BeFalse();
        result.Error.Should().Be("Pfad nicht gefunden");
    }

    [Fact]
    public async Task PreviewDirectoryAsync_EmptyPath_DoesNotTouchFileSystem()
    {
        var fs = new Mock<IFileSystemAccess>(MockBehavior.Strict);

        var result = await new FileMonitoringPreviewService(fs.Object)
            .PreviewDirectoryAsync(new WatchedDirectory { Path = " " }, new FileMonitoringDefaults());

        result.Error.Should().NotBeNull();
    }

    // --- Log scan ---

    [Fact]
    public async Task PreviewLogScanAsync_CountsPatternHitsAndSuggestionsInFiles()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/*.log")).Returns(new[]
        {
            new FileEntry("/log/old.log", "old.log", DateTime.Now.AddDays(-3)),
            new FileEntry("/log/new.log", "new.log", DateTime.Now.AddMinutes(-2)),
        });
        fs.Setup(f => f.ReadLines("/log/new.log", 5000)).Returns(new[]
        {
            "10:00 INFO start",
            "10:01 ERROR Verbindung fehlgeschlagen: connection refused",
            "10:02 System.TimeoutException: Timeout expired",
        });
        var scan = new LogScanConfig
        {
            Path = "/log/*.log",
            MaxFileAgeMinutes = 60,
            Patterns = { new LogPatternConfig { Name = "Refused", Regex = "connection refused", Severity = "Warning" } }
        };

        var result = await new FileMonitoringPreviewService(fs.Object).PreviewLogScanAsync(scan, null);

        result.Reachable.Should().BeTrue();
        result.Files.Should().HaveCount(2);
        result.Files.Single(f => f.Path == "/log/old.log").Included.Should().BeFalse();
        result.FilesScanned.Should().Be(1);
        result.LinesRead.Should().Be(3);
        result.Tail.Should().HaveCount(3);
        result.Patterns.Single().Count.Should().Be(1);
        result.Patterns.Single().Severity.Should().Be("Warning");
        result.Suggestions.Single(s => s.Name == "Exception").Count.Should().Be(1);
        result.Suggestions.Single(s => s.Name == "Zeitüberschreitung").Count.Should().Be(1);
        result.Suggestions.Single(s => s.Name == "Verbindungsfehler").Count.Should().Be(1);
        fs.Verify(f => f.ReadLines("/log/old.log", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PreviewLogScanAsync_SampleText_DoesNotTouchFileSystem()
    {
        var fs = new Mock<IFileSystemAccess>(MockBehavior.Strict);
        var scan = new LogScanConfig { Patterns = { new LogPatternConfig { Name = "Fatal", Regex = "fatal" } } };

        var result = await new FileMonitoringPreviewService(fs.Object)
            .PreviewLogScanAsync(scan, "ok\r\nFATAL: Absturz\r\nFatal again");

        result.FromSample.Should().BeTrue();
        result.LinesRead.Should().Be(3);
        result.Patterns.Single().Count.Should().Be(2);
        result.Patterns.Single().Samples.Should().Equal("FATAL: Absturz", "Fatal again");
    }

    [Fact]
    public void Match_InvalidRegex_ReportsErrorInsteadOfThrowing()
    {
        var result = FileMonitoringPreviewService.Match("Kaputt", "Critical", "(unclosed", new[] { "x" });

        result.Error.Should().StartWith("Ungültiger regulärer Ausdruck");
        result.Count.Should().Be(0);
    }

    [Fact]
    public void Match_LimitsSamplesAndShortensLongLines()
    {
        var lines = Enumerable.Range(0, 10).Select(i => "error " + new string('x', 400)).ToList();

        var result = FileMonitoringPreviewService.Match("E", "Warning", "error", lines);

        result.Count.Should().Be(10);
        result.Samples.Should().HaveCount(FileMonitoringPreviewService.MaxSamplesPerPattern);
        result.Samples[0].Length.Should().BeLessThan(FileMonitoringPreviewService.MaxLineLength + 5);
    }

    [Fact]
    public void SignatureTemplates_AreValidRegexes()
    {
        foreach (var t in FileMonitoringPreviewService.SignatureTemplates)
        {
            FileMonitoringPreviewService.Match(t.Name, t.Severity, t.Regex, new[] { "x" }).Error
                .Should().BeNull(t.Name);
        }
    }
}
