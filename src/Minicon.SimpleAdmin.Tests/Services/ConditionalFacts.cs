namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Runs only when PowerShell is available (pwsh/powershell on PATH, or SIMPLEADMIN_TEST_PWSH);
/// otherwise the test is reported as skipped instead of passing without doing anything.
/// </summary>
public sealed class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute()
    {
        if (TestPowerShell.Path == null)
            Skip = "PowerShell nicht gefunden (pwsh auf PATH oder SIMPLEADMIN_TEST_PWSH setzen).";
    }
}

/// <summary>Runs only on Linux/macOS (tests that use /bin/sh as a stand-in for PowerShell).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Benötigt /bin/sh (Linux/macOS).";
    }
}

internal static class TestPowerShell
{
    public static readonly string? Path = Find();

    private static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("SIMPLEADMIN_TEST_PWSH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : new[] { "pwsh" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
            foreach (var n in names)
            {
                var candidate = System.IO.Path.Combine(dir, n);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
