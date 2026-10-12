using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Exercises process handling, output capture, exit codes, cancel and history with /bin/sh
/// standing in for PowerShell (the start-info factory is a test seam).
/// </summary>
public sealed class ScriptRunServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-scriptrun-" + Guid.NewGuid().ToString("N"));
    private readonly string _scriptPath;

    public ScriptRunServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _scriptPath = Path.Combine(_dir, "test.ps1");
        File.WriteAllText(_scriptPath, "param([string]$Application)");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private ScriptRunService CreateService(string shellCommand, ScriptRunnerOptions? options = null)
    {
        options ??= new ScriptRunnerOptions { Enabled = true };
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
            }
        };
        return service;
    }

    private ScriptDefinition Definition(int timeoutMinutes = 0) => new()
    {
        Id = "copy",
        Name = "Copy",
        Path = _scriptPath,
        TimeoutMinutes = timeoutMinutes,
        Parameters = { new ScriptParameterDefinition { Name = "Application" } }
    };

    private static Dictionary<string, List<string>> Values(string app) => new() { ["Application"] = new List<string> { app } };

    private static async Task<ScriptRun> WaitForFinish(ScriptRunService service, string runId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var run = service.GetRun(runId)!;
            if (!run.IsRunning) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("Run did not finish");
    }

    [Fact]
    public async Task SuccessfulRun_CapturesStdoutAndStderr_AndRecordsAudit()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService("echo hallo; echo kaputt 1>&2; printf '%s' \"$SIMPLEADMIN_SCRIPT_ARGS\" | cut -f1,2");

        var started = service.Start(Definition(), Values("BZR"), "DOMAIN\\admin");
        var run = await WaitForFinish(service, started.Id);

        run.Status.Should().Be(ScriptRunStatus.Succeeded);
        run.ExitCode.Should().Be(0);
        run.StartedBy.Should().Be("DOMAIN\\admin");
        run.Arguments.Should().ContainKey("Application").WhoseValue.Should().Be("BZR");

        var (lines, _, _) = service.GetOutput(run.Id, 0);
        lines.Should().Contain(l => l.Text == "hallo" && !l.IsError);
        lines.Should().Contain(l => l.Text == "kaputt" && l.IsError);
        lines.Should().Contain(l => l.Text == "Application\ts");
        File.Exists(service.LogPath(run.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task NonZeroExitCode_MarksRunFailed()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService("echo fehler; exit 3");
        var run = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), null).Id);
        run.Status.Should().Be(ScriptRunStatus.Failed);
        run.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task SecondRunOfSameScript_IsRejectedWhileRunning_CancelStopsIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService("echo start; sleep 30");
        var first = service.Start(Definition(), Values("BZR"), "a");

        var act = () => service.Start(Definition(), Values("BZR"), "b");
        act.Should().Throw<ScriptRunException>().Which.Errors.Single().Should().Contain("läuft bereits");

        service.Cancel(first.Id, "c").Should().BeTrue();
        var run = await WaitForFinish(service, first.Id);
        run.Status.Should().Be(ScriptRunStatus.Cancelled);
        run.CancelledBy.Should().Be("c");
    }

    [Fact]
    public void ValidationErrors_PreventStart()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService("echo nie");
        var def = Definition();
        def.Parameters[0].Required = true;

        var act = () => service.Start(def, Values(""), null);
        act.Should().Throw<ScriptRunException>().Which.Errors.Single().Should().Contain("Pflichtfeld");
        service.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public async Task History_SurvivesRestart_RunningRunsBecomeInterrupted()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService("echo ok");
        var done = await WaitForFinish(service, service.Start(Definition(), Values("BZR"), "u").Id);

        var reloaded = CreateService("echo ok");
        reloaded.GetRun(done.Id)!.Status.Should().Be(ScriptRunStatus.Succeeded);
        reloaded.GetOutput(done.Id, 0).Lines.Should().Contain(l => l.Text == "ok");

        var longRunning = CreateService("sleep 30");
        var running = longRunning.Start(Definition(), Values("BZR"), "u");
        var afterRecycle = CreateService("echo x");
        afterRecycle.GetRun(running.Id)!.Status.Should().Be(ScriptRunStatus.Interrupted);
        longRunning.Cancel(running.Id, null);
        await WaitForFinish(longRunning, running.Id);
    }

    [Fact]
    public void ScriptOutsideAllowedDirectories_IsRejected()
    {
        var policy = new ScriptPathPolicy(Path.Combine(_dir, "uploads"), new[] { Path.Combine(_dir, "allowed") }, Array.Empty<string>());
        policy.ValidateScriptPath(_scriptPath).Should().Contain("außerhalb");
        policy.ValidateScriptPath(Path.Combine(_dir, "allowed", "x.ps1")).Should().BeNull();
        policy.ValidateScriptPath(Path.Combine(_dir, "allowed", "..", "test.ps1")).Should().Contain("außerhalb");
        policy.ValidateScriptPath(Path.Combine(_dir, "uploads", "up.ps1")).Should().BeNull("the upload directory is always allowed");
        policy.ValidateScriptPath(Path.Combine(_dir, "allowed", "x.bat")).Should().Contain(".ps1");
        policy.ValidateScriptPath("relative.ps1").Should().Contain("absolut");
    }

    [Fact]
    public void DefinitionStore_RoundTrip()
    {
        var store = new ScriptDefinitionStore(Path.Combine(_dir, "scripts.json"));
        var def = Definition();
        def.Parameters[0].Type = ScriptParameterType.Choice;
        def.Parameters[0].Choices = new List<string> { "BZR", "ZSTv" };
        store.Save(def);

        var loaded = new ScriptDefinitionStore(Path.Combine(_dir, "scripts.json")).Get("copy")!;
        loaded.Name.Should().Be("Copy");
        loaded.Parameters.Single().Type.Should().Be(ScriptParameterType.Choice);
        loaded.Parameters.Single().Choices.Should().Equal("BZR", "ZSTv");
        File.ReadAllText(Path.Combine(_dir, "scripts.json")).Should().Contain("\"type\": \"Choice\"");

        store.Delete("copy").Should().BeTrue();
        store.GetAll().Should().BeEmpty();
    }
}

public class ScriptRunnerOptionsTests
{
    [Fact]
    public void ScriptRunner_IsEnabledByDefault_AndCanBeSwitchedOff()
    {
        new ScriptRunnerOptions().Enabled.Should().BeTrue();

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ScriptRunner:Enabled"] = "false" })
            .Build();
        Microsoft.Extensions.Configuration.ConfigurationBinder
            .Get<ScriptRunnerOptions>(config.GetSection(ScriptRunnerOptions.SectionName))!.Enabled.Should().BeFalse();
    }
}
