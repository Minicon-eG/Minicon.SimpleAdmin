using System.Diagnostics;
using FluentAssertions;
using Minicon.SimpleAdmin.Checkers;

namespace Minicon.SimpleAdmin.Tests.Services;

public class EventLogQueryScriptTests
{
    private const string Hostile = "System'}; $global:pwned = 1; @{x='";

    [Theory]
    [InlineData("System", "'System'")]
    [InlineData("Microsoft-Windows-PowerShell/Operational", "'Microsoft-Windows-PowerShell/Operational'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a’b", "'a’’b'")]
    [InlineData("a\nb", "'ab'")]
    public void QuotePowerShellLiteral_EscapesQuotes(string input, string expected) =>
        EventLogChecker.QuotePowerShellLiteral(input).Should().Be(expected);

    [Fact]
    public void BuildEventQueryScript_EmbedsLogNameOnlyAsLiteral()
    {
        var script = EventLogChecker.BuildEventQueryScript(Hostile, 60, "1,2");
        script.Should().Contain("LogName='System''}; $global:pwned = 1; @{x='''");
        script.Should().Contain("AddMinutes(-60)").And.Contain("Level=@(1,2)");
    }

    /// <summary>With PowerShell available, the escaped literal must evaluate back to the raw string without side effects.</summary>
    [Fact]
    public void QuotedLiteral_RoundTripsInRealPowerShell()
    {
        var pwsh = Environment.GetEnvironmentVariable("SIMPLEADMIN_TEST_PWSH");
        if (string.IsNullOrEmpty(pwsh) || !File.Exists(pwsh)) return;

        var psi = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"$v = {EventLogChecker.QuotePowerShellLiteral(Hostile)}; if ($global:pwned) {{ 'PWNED' }} else {{ $v }}" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        output.Should().Be(Hostile);
    }
}
