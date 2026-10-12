using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.WebUI.Controllers;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Exercises process handling, output capture, exit codes, cancel, timeout and history with /bin/sh
/// standing in for PowerShell (the start-info factory is a test seam).
/// </summary>
public sealed class ScriptRunServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-scriptrun-" + Guid.NewGuid().ToString("N"));
    private readonly string _scriptPath;
    private readonly List<ScriptRunService> _services = new();

    public ScriptRunServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _scriptPath = Path.Combine(_dir, "test.ps1");
        File.WriteAllText(_scriptPath, "param([string]$Application)");
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private ScriptRunService CreateService(string shellCommand, ScriptRunnerOptions? options = null)
    {
        options ??= new ScriptRunnerOptions();
        var policy = new ScriptPathPolicy(Path.Combine(_dir, "uploads"), Array.Empty<string>(), Array.Empty<string>());
        var service = new ScriptRunService(options, policy, Path.Combine(_dir, "runs"), NullLogger<ScriptRunService>.Instance)
        {
            StartInfoFactory = (_, encoded) =>
            {
                var psi = new ProcessStartInfo("/bin/sh")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(shellCommand);
                psi.Environment[ScriptArgumentBuilder.ArgsVariable] = encoded;
                return psi;
            },
            StreamDrainGrace = TimeSpan.FromMilliseconds(500)
        };
        _services.Add(service);
        return service;
    }

    private ScriptDefinition Definition(string id = "copy", int timeoutMinutes = 0) => new()
    {
        Id = id,
        Name = "Copy " + id,
        Path = _scriptPath,
        TimeoutMinutes = timeoutMinutes,
        Parameters = { new ScriptParameterDefinition { Name = "Application" } }
    };

    private static Dictionary<string, List<string>> Values(string app) => new() { ["Application"] = new List<string> { app } };

    private static async Task<ScriptRun> WaitForFinish(ScriptRunService service, string runId, int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var run = service.GetRun(runId)!;
            if (!run.IsRunning) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("Run did not finish");
    }

    [UnixFact]
    public async Task SuccessfulRun_CapturesStdoutAndStderr_AndRecordsAudit()
    {
        var service = CreateService("echo hallo; echo kaputt 1>&2; printf '%s' \"$SIMPLEADMIN_SCRIPT_ARGS\" | cut -f1,2");

        var started = service.Start(Definition(), Values("BZR"), "DOMAIN\\admin");
        var run = await WaitForFinish(service, started.Id);

        run.Status.Should().Be(ScriptRunStatus.Succeeded);
        run.ExitCode.Should().Be(0);
        run.StartedBy.Should().Be("DOMAIN\\admin");
        run.Arguments.Should().ContainKey("Application").WhoseValue.Should().Be("BZR");
        run.CommandLine.Should().Be(".\\test.ps1 -Application \"BZR\"");

        var page = service.GetOutput(run.Id, 0);
        page.Lines.Should().Contain(l => l.Text == "hallo" && !l.IsError);
        page.Lines.Should().Contain(l => l.Text == "kaputt" && l.IsError);
        page.Lines.Should().Contain(l => l.Text == "Application\ts");
        page.Lines.Last().Text.Should().Be("=== Beendet: Erfolgreich, Exit-Code 0 ===", "the final line is part of the stream, not only of the file");
        File.ReadAllText(service.LogPath(run.Id)).Should().Contain("| hallo").And.Contain("! kaputt").And.Contain("=== Beendet");
    }

    [UnixFact]
    public async Task NonZeroExitCode_MarksRunFailed()
    {
        var service = CreateService("echo fehler; exit 3");
        var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id);
        run.Status.Should().Be(ScriptRunStatus.Failed);
        run.ExitCode.Should().Be(3);
    }

    [UnixFact]
    public async Task SecondRunOfSameScript_IsRejectedWhileRunning_CancelStopsIt()
    {
        var service = CreateService("echo start; sleep 30");
        var first = service.Start(Definition(), Values("BZR"), "a");

        var act = () => service.Start(Definition(), Values("BZR"), "b");
        act.Should().Throw<ScriptRunException>().Which.Errors.Single().Should().Contain("läuft bereits");

        service.Cancel(first.Id, "c").Should().BeTrue();
        var run = await WaitForFinish(service, first.Id);
        run.Status.Should().Be(ScriptRunStatus.Cancelled);
        run.CancelledBy.Should().Be("c");
    }

    [UnixFact]
    public async Task MaxConcurrentRuns_LimitsDifferentScripts()
    {
        var service = CreateService("sleep 30", new ScriptRunnerOptions { MaxConcurrentRuns = 1 });
        var first = service.Start(Definition("a"), Values("BZR"), null);

        var act = () => service.Start(Definition("b"), Values("BZR"), null);
        act.Should().Throw<ScriptRunException>().Which.Errors.Single().Should().Contain("Maximum 1");

        service.Cancel(first.Id, null);
        await WaitForFinish(service, first.Id);
    }

    [UnixFact]
    public async Task Timeout_KillsTheScript()
    {
        var service = CreateService("echo los; sleep 30");
        service.TimeoutUnit = TimeSpan.FromMilliseconds(300);

        var run = await WaitForFinish(service, service.Start(Definition(timeoutMinutes: 1), Values("BZR"), null).Id);

        run.Status.Should().Be(ScriptRunStatus.TimedOut);
        service.GetOutput(run.Id, 0).Lines.Should().Contain(l => l.Text.Contains("Zeitlimit"));
    }

    [UnixFact]
    public async Task BackgroundChildHoldingStdout_DoesNotKeepTheRunOpen()
    {
        // The background sleep inherits stdout; waiting for EOF would block for 30 s.
        var service = CreateService("sleep 30 & echo fertig");
        var sw = Stopwatch.StartNew();

        var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id, seconds: 10);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        run.Status.Should().Be(ScriptRunStatus.Succeeded);
        service.GetOutput(run.Id, 0).Lines.Should().Contain(l => l.Text == "fertig");
    }

    [UnixFact]
    public async Task RingBuffer_KeepsNewestLines_ReportsSkipped_TailAndLogStayComplete()
    {
        var service = CreateService("i=1; while [ $i -le 300 ]; do echo line$i; i=$((i+1)); done",
            new ScriptRunnerOptions { MaxOutputLines = 100 });
        var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id);

        var fromStart = service.GetOutput(run.Id, 0);
        fromStart.Skipped.Should().BeGreaterThan(200);
        fromStart.Lines.Should().HaveCount(100);
        fromStart.Lines[^2].Text.Should().Be("line300");

        var tail = service.GetOutput(run.Id, null);
        tail.Lines.Should().HaveCount(100);
        tail.Skipped.Should().Be(tail.FirstIndex).And.BeGreaterThan(0);

        File.ReadLines(service.LogPath(run.Id)).Should().Contain(l => l.EndsWith("| line1"), "the log file keeps everything");
    }

    [UnixFact]
    public void ValidationErrors_PreventStart()
    {
        var service = CreateService("echo nie");
        var def = Definition();
        def.Parameters[0].Required = true;

        var act = () => service.Start(def, Values(""), null);
        act.Should().Throw<ScriptRunException>().Which.Errors.Single().Should().Contain("Pflichtfeld");
        service.GetHistory().Should().BeEmpty();
    }

    [UnixFact]
    public async Task FinishedRunNotInMemory_ServesTailFromLogFile_AndAsksLivePollersToResync()
    {
        var service = CreateService("echo ok");
        var done = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), "u").Id);

        var reloaded = CreateService("echo ok"); // new process: nothing in memory
        reloaded.GetRun(done.Id)!.Status.Should().Be(ScriptRunStatus.Succeeded);

        var tail = reloaded.GetOutput(done.Id, null);
        tail.Lines.Should().Contain(l => l.Text == "ok");
        tail.Skipped.Should().Be(0);
        reloaded.GetOutput(done.Id, 5).Resync.Should().BeTrue();
    }

    [UnixFact]
    public async Task OverlappedRecycle_BothProcessesKeepTheirRuns_NewestStatusWins()
    {
        var oldProcess = CreateService("sleep 30");
        var oldRun = oldProcess.Start(Definition("a"), Values("BZR"), "u");

        // New process starts while the old one is still running its script.
        var newProcess = CreateService("echo neu");
        newProcess.GetRun(oldRun.Id)!.Status.Should().Be(ScriptRunStatus.Interrupted);
        var newRun = await WaitForFinish(newProcess, newProcess.Start(Definition("b"), Values("BZR"), "u").Id);

        // The old process finishes its run afterwards — its newer status must win, and the new run must survive.
        oldProcess.Cancel(oldRun.Id, "x");
        await WaitForFinish(oldProcess, oldRun.Id);

        var fromDisk = CreateService("echo x");
        fromDisk.GetRun(oldRun.Id)!.Status.Should().Be(ScriptRunStatus.Cancelled);
        fromDisk.GetRun(newRun.Id)!.Status.Should().Be(ScriptRunStatus.Succeeded);
    }

    [UnixFact]
    public async Task History_IsTrimmed_AndLogsOfDroppedRunsAreDeleted()
    {
        var service = CreateService("echo ok", new ScriptRunnerOptions { HistorySize = 10 });
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id);
            ids.Add(run.Id);
            await Task.Delay(10);
        }

        service.GetHistory(max: 100).Should().HaveCount(10);
        service.GetRun(ids[0]).Should().BeNull();
        File.Exists(service.LogPath(ids[0])).Should().BeFalse();
        File.Exists(service.LogPath(ids[^1])).Should().BeTrue();
    }

    [UnixFact]
    public async Task LogDownload_ReturnsTheCompleteLogFile()
    {
        var service = CreateService("i=1; while [ $i -le 300 ]; do echo line$i; i=$((i+1)); done",
            new ScriptRunnerOptions { MaxOutputLines = 100 });
        var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id);
        var store = new ScriptDefinitionStore(Path.Combine(_dir, "scripts.json"), NullLogger<ScriptDefinitionStore>.Instance);
        var controller = new ScriptsController(store, service,
            new ScriptPathPolicy(_dir, Array.Empty<string>(), Array.Empty<string>()), NullLogger<ScriptsController>.Instance);

        var result = controller.Log(run.Id).Should().BeOfType<PhysicalFileResult>().Subject;

        result.FileName.Should().Be(service.LogPath(run.Id));
        File.ReadLines(result.FileName).Count(l => l.Contains("| line")).Should().Be(300);
    }

    [Fact]
    public void LogLines_RoundTrip_AndLegacyFormatIsRead()
    {
        var line = new ScriptOutputLine(new DateTime(2026, 10, 13, 12, 0, 1, 123, DateTimeKind.Utc), true, "a | b ! c");
        var parsed = ScriptRunService.ParseLogLine(ScriptRunService.FormatLogLine(line))!;
        parsed.Should().Be(line);

        var legacy = ScriptRunService.ParseLogLine("2026-10-12T21:02:13.0000000Z|O|alt")!;
        legacy.Text.Should().Be("alt");
        legacy.IsError.Should().BeFalse();
        ScriptRunService.ParseLogLine("kein Logformat").Should().BeNull();
    }

    [Fact]
    public void ReadLogTail_ReadsOnlyTheEnd()
    {
        var path = Path.Combine(_dir, "big.log");
        var start = DateTime.UtcNow;
        File.WriteAllLines(path, Enumerable.Range(1, 50_000)
            .Select(i => ScriptRunService.FormatLogLine(new ScriptOutputLine(start, false, "line" + i))));

        var (lines, complete) = ScriptRunService.ReadLogTail(path, 10);

        lines.Select(l => l.Text).Should().Equal(Enumerable.Range(49_991, 10).Select(i => "line" + i));
        complete.Should().BeFalse();
        ScriptRunService.ReadLogTail(path, 100_000).Complete.Should().BeTrue();
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("allowed2", false)]   // sibling with the same prefix
    [InlineData("allowedX/../allowed", true)]
    public void PathPolicy_DirectoryBoundaries(string relative, bool allowed)
    {
        var policy = new ScriptPathPolicy(Path.Combine(_dir, "uploads"), new[] { Path.Combine(_dir, "allowed") }, Array.Empty<string>());
        var path = Path.Combine(_dir, relative, "x.ps1");
        (policy.ValidateScriptPath(path) == null).Should().Be(allowed);
    }

    [Fact]
    public void PathPolicy_RulesForScripts()
    {
        var policy = new ScriptPathPolicy(Path.Combine(_dir, "uploads"), new[] { Path.Combine(_dir, "allowed") }, Array.Empty<string>());
        policy.ValidateScriptPath(_scriptPath).Should().Contain("außerhalb");
        policy.ValidateScriptPath(Path.Combine(_dir, "allowed", "..", "test.ps1")).Should().Contain("außerhalb");
        policy.ValidateScriptPath(Path.Combine(_dir, "uploads", "up.ps1")).Should().BeNull("the upload directory is always allowed");
        policy.ValidateScriptPath(Path.Combine(_dir, "allowed", "x.bat")).Should().Contain(".ps1");
        policy.ValidateScriptPath("relative.ps1").Should().Contain("absolut");
    }

    [Fact]
    public void PathPolicy_RootAllowsEverythingBelow()
    {
        var root = Path.GetPathRoot(_dir)!;
        ScriptPathPolicy.IsUnder(Path.Combine(_dir, "x.ps1"), root).Should().BeTrue();
        new ScriptPathPolicy(_dir, Array.Empty<string>(), new[] { root }).CanBrowse(_dir).Should().BeTrue();
    }

    [Fact]
    public void DefinitionStore_RoundTrip()
    {
        var store = new ScriptDefinitionStore(Path.Combine(_dir, "scripts.json"), NullLogger<ScriptDefinitionStore>.Instance);
        var def = Definition();
        def.Parameters[0].Type = ScriptParameterType.Choice;
        def.Parameters[0].Choices = new List<string> { "BZR", "ZSTv" };
        store.Save(def);

        var loaded = new ScriptDefinitionStore(Path.Combine(_dir, "scripts.json"), NullLogger<ScriptDefinitionStore>.Instance).Get("copy")!;
        loaded.Name.Should().Be("Copy copy");
        loaded.Parameters.Single().Type.Should().Be(ScriptParameterType.Choice);
        loaded.Parameters.Single().Choices.Should().Equal("BZR", "ZSTv");
        File.ReadAllText(Path.Combine(_dir, "scripts.json")).Should().Contain("\"type\": \"Choice\"");

        store.Delete("copy").Should().BeTrue();
        store.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void DefinitionStore_CorruptFile_IsReportedAndNeverOverwritten()
    {
        var path = Path.Combine(_dir, "scripts.json");
        File.WriteAllText(path, "{ kaputt");
        var store = new ScriptDefinitionStore(path, NullLogger<ScriptDefinitionStore>.Instance);

        store.GetAll().Should().BeEmpty();
        store.LoadError.Should().Contain("konnte nicht gelesen werden");
        var act = () => store.Save(Definition());
        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(path).Should().Be("{ kaputt");
    }

    [Fact]
    public void DefinitionStore_PicksUpManualEdits()
    {
        var path = Path.Combine(_dir, "scripts.json");
        var store = new ScriptDefinitionStore(path, NullLogger<ScriptDefinitionStore>.Instance);
        store.Save(Definition("a"));

        var json = File.ReadAllText(path).Replace("\"Copy a\"", "\"Von Hand\"");
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        store.Get("a")!.Name.Should().Be("Von Hand");
    }
}

public class ScriptRunnerOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void EnabledByDefault_WithAuthentication()
    {
        var options = ScriptRunnerOptions.Resolve(Config(), authenticationEnabled: true);
        options.Enabled.Should().BeTrue();
        options.DisabledReason.Should().BeNull();
    }

    [Fact]
    public void ExplicitlyDisabled()
    {
        var options = ScriptRunnerOptions.Resolve(Config(("ScriptRunner:Enabled", "false")), authenticationEnabled: true);
        options.Enabled.Should().BeFalse();
        options.DisabledReason.Should().Contain("abgeschaltet");
    }

    [Fact]
    public void SwitchedOffAutomatically_WithoutAuthentication_UnlessAllowed()
    {
        var off = ScriptRunnerOptions.Resolve(Config(), authenticationEnabled: false);
        off.Enabled.Should().BeFalse();
        off.DisabledReason.Should().Contain("ohne Anmeldung");

        var allowed = ScriptRunnerOptions.Resolve(Config(("ScriptRunner:AllowWithoutAuthentication", "true")), authenticationEnabled: false);
        allowed.Enabled.Should().BeTrue();
    }
}
