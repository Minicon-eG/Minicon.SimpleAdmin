using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Tests for the offset tracking (stateful log de-duplication): only the log lines written since the
/// last cycle are reported, the read position survives a restart, and a rotated file is read again.
/// </summary>
public class LogFileOffsetTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 22, 0, 0, DateTimeKind.Local);

    private static FileShareChecker Build(IFileSystemAccess fs, ILogFileOffsetStore? store = null)
        => new(fs, NullLogger<FileShareChecker>.Instance, store);

    private static ServerFileMonitoringConfig Config() => new()
    {
        Enabled = true,
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

    // --- Store ---

    [Fact]
    public void JsonStore_RoundTripsTheOffsets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-offsets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonLogFileOffsetStore(path, NullLogger<JsonLogFileOffsetStore>.Instance);
            store.Set("/log/a.log", new LogFileOffset { Offset = 128, Length = 256, LastWriteTime = Now });

            store.Get("/log/a.log")!.Offset.Should().Be(128);
            store.Get("/log/unbekannt").Should().BeNull();

            store.Save();
            File.Exists(path).Should().BeTrue();

            // A new process reads the same positions back — the offset survives the restart.
            var reloaded = new JsonLogFileOffsetStore(path, NullLogger<JsonLogFileOffsetStore>.Instance);
            reloaded.Get("/log/a.log")!.Offset.Should().Be(128);
            reloaded.Get("/log/a.log")!.Length.Should().Be(256);
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
    public void JsonStore_BrokenFile_StartsWithoutOffsets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-offsets-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ kaputt");

            var store = new JsonLogFileOffsetStore(path, NullLogger<JsonLogFileOffsetStore>.Instance);

            store.Get("/log/a.log").Should().BeNull();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- Checker: only the new lines are reported ---

    [Fact]
    public void Check_SecondCycle_ReportsOnlyTheNewLines()
    {
        var store = new InMemoryLogFileOffsetStore();
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Now) });
        fs.Setup(f => f.ReadTail("/log/a.log", It.IsAny<int>()))
            .Returns(new LogFileReadResult(new[] { "alte Zeile haengt fest" }, 100, 100, Now));
        fs.Setup(f => f.ReadFrom("/log/a.log", 100, It.IsAny<int>()))
            .Returns(new LogFileReadResult(new[] { "neue Zeile haengt fest" }, 150, 150, Now));

        var checker = Build(fs.Object, store);
        var config = Config();

        var first = checker.Check(config, new FileMonitoringDefaults(), Now);
        first.LogMatches.Should().ContainSingle().Which.Count.Should().Be(1);
        first.LogMatches[0].SampleLine.Should().Be("alte Zeile haengt fest");

        var second = checker.Check(config, new FileMonitoringDefaults(), Now);
        second.LogMatches.Should().ContainSingle().Which.Count.Should().Be(1);
        second.LogMatches[0].SampleLine.Should().Be("neue Zeile haengt fest");

        // The third cycle has nothing new — the same hit is not reported again.
        fs.Setup(f => f.ReadFrom("/log/a.log", 150, It.IsAny<int>()))
            .Returns(new LogFileReadResult(Array.Empty<string>(), 150, 150, Now));

        var third = checker.Check(config, new FileMonitoringDefaults(), Now);
        third.LogMatches.Should().BeEmpty();

        store.Get("/log/a.log")!.Offset.Should().Be(150);
    }

    [Fact]
    public void Check_OffsetTrackingDisabled_ReadsTheWholeTailAgain()
    {
        var store = new InMemoryLogFileOffsetStore();
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Now) });
        fs.Setup(f => f.ReadLines("/log/a.log", It.IsAny<int>()))
            .Returns(new[] { "alte Zeile haengt fest" });

        var checker = Build(fs.Object, store);
        var defaults = new FileMonitoringDefaults { OffsetTrackingEnabled = false };
        var config = Config();

        checker.Check(config, defaults, Now).LogMatches.Should().ContainSingle();
        checker.Check(config, defaults, Now).LogMatches.Should().ContainSingle();

        // No position is remembered, the tail is read in every cycle.
        store.Count.Should().Be(0);
    }

    [Fact]
    public void Check_RotatedFile_IsCountedAndReadAgain()
    {
        var store = new InMemoryLogFileOffsetStore();
        store.Set("/log/a.log", new LogFileOffset { Offset = 500, Length = 500, LastWriteTime = Now });

        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Now) });
        // The file was rotated: it is shorter than the remembered position, so it is read from the start.
        fs.Setup(f => f.ReadFrom("/log/a.log", 500, It.IsAny<int>()))
            .Returns(new LogFileReadResult(new[] { "neue Datei haengt fest" }, 20, 20, Now));

        var state = Build(fs.Object, store).Check(Config(), new FileMonitoringDefaults(), Now);

        state.LogFilesRotated.Should().Be(1);
        state.LogMatches.Should().ContainSingle().Which.SampleLine.Should().Be("neue Datei haengt fest");
        store.Get("/log/a.log")!.Offset.Should().Be(20);
    }

    [Fact]
    public void Check_UnreadableFile_KeepsThePosition()
    {
        var store = new InMemoryLogFileOffsetStore();
        store.Set("/log/a.log", new LogFileOffset { Offset = 300, Length = 300, LastWriteTime = Now });

        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/a.log"))
            .Returns(new[] { new FileEntry("/log/a.log", "a.log", Now) });
        fs.Setup(f => f.ReadFrom("/log/a.log", 300, It.IsAny<int>()))
            .Throws(new IOException("share offline"));

        var state = Build(fs.Object, store).Check(Config(), new FileMonitoringDefaults(), Now);

        state.LogMatches.Should().BeEmpty();
        store.Get("/log/a.log")!.Offset.Should().Be(300, "ein Fehler darf den Versatz nicht zurücksetzen");
    }

    // --- FileSystemAccess: reading from an offset ---

    [Fact]
    public void ReadFrom_ReturnsOnlyTheAppendedLinesAndTheNewPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-log-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "alt\n");
            var first = new FileSystemAccess().ReadFrom(path, 0, 0);
            first.Lines.Should().Equal("alt");
            first.Offset.Should().Be(4);

            File.AppendAllText(path, "neu\n");
            var second = new FileSystemAccess().ReadFrom(path, first.Offset, 0);
            second.Lines.Should().Equal("neu");
            second.Offset.Should().Be(8);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFrom_TruncatedFile_ReadsFromTheBeginning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-log-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "ganz viel text\n");
            var store = new InMemoryLogFileOffsetStore();
            store.Set(path, new LogFileOffset { Offset = 100, Length = 100, LastWriteTime = Now });

            // The file was truncated and rewritten — the old position no longer exists.
            File.WriteAllText(path, "neu\n");

            var result = new FileSystemAccess().ReadFrom(path, 100, 0);

            result.Lines.Should().Equal("neu");
            result.Offset.Should().Be(4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFrom_MaxLines_LeavesTheRestForTheNextCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-log-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "eins\nzwei\ndrei\n");

            var first = new FileSystemAccess().ReadFrom(path, 0, 2);
            first.Lines.Should().Equal("eins", "zwei");
            first.Offset.Should().Be(10);

            var second = new FileSystemAccess().ReadFrom(path, first.Offset, 2);
            second.Lines.Should().Equal("drei");
            second.Offset.Should().Be(15);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTail_ReturnsTheLastLinesAndThePositionBehindThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-log-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "eins\nzwei\ndrei\n");

            var result = new FileSystemAccess().ReadTail(path, 2);

            result.Lines.Should().Equal("zwei", "drei");
            result.Offset.Should().Be(15);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTail_IncompleteLastLine_StaysForTheNextCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sa-log-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "fertig\nunvollstaendig");

            var result = new FileSystemAccess().ReadTail(path, 0);

            result.Lines.Should().Equal("fertig", "unvollstaendig");
            result.Offset.Should().Be(7, "die unvollständige Zeile wird erst im nächsten Zyklus gelesen");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
