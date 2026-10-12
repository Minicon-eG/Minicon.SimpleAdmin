using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// End-to-end check of the bootstrap with a real PowerShell. Runs only when PowerShell is
/// available (pwsh on PATH, or SIMPLEADMIN_TEST_PWSH pointing to the executable); otherwise skipped.
/// </summary>
public sealed class ScriptRunPowerShellIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-pwsh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [PowerShellFact]
    public async Task Bootstrap_SplatsTypedArguments_WithoutInjection_AndPropagatesExitCode()
    {
        var pwsh = TestPowerShell.Path!;

        Directory.CreateDirectory(_dir);
        var script = Path.Combine(_dir, "Echo Params.ps1");
        await File.WriteAllTextAsync(script, """
            [CmdletBinding()]
            param(
                [string]$SourceDate,
                [ValidateSet('BZR','ZSTv')][string]$Application,
                [string[]]$IncludeAuthorities,
                [int]$Wait = 5,
                [switch]$DryRun,
                [switch]$Force,
                [string]$Evil
            )
            Write-Output "SourceDate=$SourceDate"
            Write-Output "Application=$Application"
            Write-Output ("Authorities=" + ($IncludeAuthorities -join '|') + " Count=" + $IncludeAuthorities.Count)
            Write-Output ("Wait=" + $Wait + " Type=" + $Wait.GetType().Name)
            Write-Output "DryRun=$($DryRun.IsPresent) Force=$($Force.IsPresent)"
            Write-Output "Evil=$Evil"
            Write-Host "Umlaute: äöüß"
            Write-Error "nicht fatal"
            if (Test-Path variable:pwned) { Write-Output "PWNED" }
            exit 7
            """);

        var options = new ScriptRunnerOptions { Enabled = true, PowerShellPath = pwsh };
        var policy = new ScriptPathPolicy(Path.Combine(_dir, "up"), Array.Empty<string>(), Array.Empty<string>());
        using var service = new ScriptRunService(options, policy, Path.Combine(_dir, "runs"), NullLogger<ScriptRunService>.Instance);

        var definition = PowerShellScriptParser.CreateDefinition(script, await File.ReadAllTextAsync(script));
        var input = new Dictionary<string, List<string>>
        {
            ["SourceDate"] = new() { "2025-07-20" },
            ["Application"] = new() { "zstv" },
            ["IncludeAuthorities"] = new() { "R3100S\nR1234S" },
            ["Wait"] = new() { "10" },
            ["DryRun"] = new() { "true" },
            ["Force"] = new() { "false" },
            ["Evil"] = new() { "a'; $global:pwned = 1; '\"$(Set-Variable pwned 1 -Scope Global)" }
        };

        var started = service.Start(definition, input, "tester");
        var sw = Stopwatch.StartNew();
        ScriptRun run;
        do
        {
            await Task.Delay(100);
            run = service.GetRun(started.Id)!;
        } while (run.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(60));

        var output = service.GetOutput(run.Id, 0).Lines;
        var text = string.Join("\n", output.Select(l => (l.IsError ? "E: " : "") + l.Text));

        run.ExitCode.Should().Be(7, text);
        run.Status.Should().Be(ScriptRunStatus.Failed);
        text.Should().Contain("SourceDate=2025-07-20");
        text.Should().Contain("Application=ZSTv");
        text.Should().Contain("Authorities=R3100S|R1234S Count=2");
        text.Should().Contain("Wait=10 Type=Int32");
        text.Should().Contain("DryRun=True Force=False");
        text.Should().Contain("Evil=a'; $global:pwned = 1; '\"$(Set-Variable pwned 1 -Scope Global)");
        text.Should().Contain("Umlaute: äöüß");
        text.Should().NotContain("PWNED");
        output.Should().Contain(l => l.IsError && l.Text.Contains("nicht fatal"));
        text.Should().NotContain("\u001b[", "ANSI colour codes are stripped");
    }
}
