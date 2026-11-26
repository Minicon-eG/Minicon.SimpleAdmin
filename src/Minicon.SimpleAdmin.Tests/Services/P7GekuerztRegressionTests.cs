using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public sealed class P7GekuerztRegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void SignatureScan_UsesOnlyTheConfiguredTail()
    {
        var lines = new[] { "ERROR outside the configured tail", "ok 1", "ok 2" };
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/app.log")).Returns(new[]
        {
            new FileEntry("/log/app.log", "app.log", Now.AddMinutes(-5))
        });
        fs.Setup(f => f.ReadLines("/log/app.log", It.IsAny<int>()))
            .Returns((string _, int maxLines) => maxLines <= 0 ? lines : lines.TakeLast(maxLines).ToArray());

        var config = new ServerFileMonitoringConfig
        {
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Bestand",
                    Path = "/log/app.log",
                    MaxLines = 2,
                    Patterns = { new LogPatternConfig { Name = "Fehler", Regex = "ERROR" } }
                }
            }
        };

        var state = new FileShareChecker(fs.Object, NullLogger<FileShareChecker>.Instance)
            .Check(config, new FileMonitoringDefaults(), Now);

        state.LogMatches.Should().BeEmpty();
    }

    [Fact]
    public void SignatureScan_WithZeroMaxLines_ReadsTheWholeFile()
    {
        var lines = Enumerable.Repeat("ok", 20_000).Prepend("ERROR at the start").ToArray();
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/app.log")).Returns(new[]
        {
            new FileEntry("/log/app.log", "app.log", Now.AddMinutes(-5))
        });
        fs.Setup(f => f.ReadLines("/log/app.log", It.IsAny<int>()))
            .Returns((string _, int maxLines) => maxLines <= 0 ? lines : lines.TakeLast(maxLines).ToArray());

        var config = new ServerFileMonitoringConfig
        {
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Bestand",
                    Path = "/log/app.log",
                    MaxLines = 0,
                    Patterns = { new LogPatternConfig { Name = "Fehler", Regex = "ERROR" } }
                }
            }
        };

        var state = new FileShareChecker(fs.Object, NullLogger<FileShareChecker>.Instance)
            .Check(config, new FileMonitoringDefaults(), Now);

        state.LogMatches.Should().ContainSingle().Which.Count.Should().Be(1);
    }

    [Fact]
    public void PendingRules_UseOnlyTheNewestMatchingFile()
    {
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/*.log")).Returns(new[]
        {
            new FileEntry("/log/old.log", "old.log", Now.AddHours(-2)),
            new FileEntry("/log/new.log", "new.log", Now.AddHours(-1))
        });
        fs.Setup(f => f.ReadLines("/log/old.log", It.IsAny<int>())).Returns(new[]
        {
            "2026-11-26 10:00:00.000 waiting for Result"
        });
        fs.Setup(f => f.ReadLines("/log/new.log", It.IsAny<int>())).Returns(Array.Empty<string>());

        var config = new ServerFileMonitoringConfig
        {
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Behörde",
                    Path = "/log/*.log",
                    PendingRules =
                    {
                        new LogPendingRule
                        {
                            Name = "Lauf",
                            StartRegex = "waiting for Result",
                            EndRegex = "Verarbeitungsergebnis:"
                        }
                    }
                }
            }
        };

        var state = new FileShareChecker(fs.Object, NullLogger<FileShareChecker>.Instance)
            .Check(config, new FileMonitoringDefaults(), Now);

        state.PendingRuns.Should().BeEmpty();
        state.RunningLogRuns.Should().BeEmpty();
        state.AbandonedRuns.Should().BeEmpty();
    }

    [Fact]
    public void ExpectedRules_UseOnlyTheNewestFileWithoutAnEvidenceCountError()
    {
        var files = Enumerable.Range(0, 40)
            .Select(i => new FileEntry($"/log/{i:00}.log", $"{i:00}.log", Now.AddMinutes(-i)))
            .ToArray();
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/*.log")).Returns(files);
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>())).Returns(Array.Empty<string>());
        fs.Setup(f => f.ReadLines("/log/00.log", It.IsAny<int>())).Returns(new[]
        {
            "2026-11-25 22:05:00.000 Verarbeitungsergebnis: OK"
        });

        var config = new ServerFileMonitoringConfig
        {
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Behörde",
                    Path = "/log/*.log",
                    ExpectedRuns =
                    {
                        new ExpectedRunRule
                        {
                            Name = "Abendlauf",
                            MatchRegex = "Verarbeitungsergebnis:",
                            Schedule = "Mon-Fri",
                            ExpectedTime = "22:00",
                            GraceMinutes = 120
                        }
                    }
                }
            }
        };

        var state = new FileShareChecker(fs.Object, NullLogger<FileShareChecker>.Instance)
            .Check(config, new FileMonitoringDefaults(), Now);

        state.MissingRuns.Should().BeEmpty();
        state.EvaluationErrors.Should().BeEmpty();
        fs.Verify(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
        fs.Verify(f => f.ReadLines("/log/00.log", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Preview_PendingRulesMatchTheWorkersSingleFileSelection()
    {
        var now = DateTime.Now;
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/*.log")).Returns(new[]
        {
            new FileEntry("/log/old.log", "old.log", now.AddHours(-2)),
            new FileEntry("/log/new.log", "new.log", now.AddHours(-1))
        });
        fs.Setup(f => f.ReadLines("/log/old.log", It.IsAny<int>())).Returns(new[]
        {
            now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss.fff") + " waiting for Result"
        });
        fs.Setup(f => f.ReadLines("/log/new.log", It.IsAny<int>())).Returns(new[]
        {
            now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss.fff") + " waiting for Result"
        });
        var scan = new LogScanConfig
        {
            Name = "Behörde",
            Path = "/log/*.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:"
                }
            }
        };

        var preview = await new FileMonitoringPreviewService(fs.Object).PreviewLogScanAsync(scan, null);

        preview.PendingRuns.Should().ContainSingle().Which.LogPath.Should().Be("/log/new.log");
        preview.RunningLogRuns.Should().BeEmpty();
        preview.AbandonedRuns.Should().BeEmpty();
    }
}
