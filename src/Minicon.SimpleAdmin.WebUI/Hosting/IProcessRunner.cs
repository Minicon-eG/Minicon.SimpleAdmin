using System.Diagnostics;

namespace Minicon.SimpleAdmin.WebUI.Hosting;

/// <summary>
/// Seam for the external tools the Windows-service installer shells out to
/// (<c>sc.exe</c>, <c>netsh.exe</c>, <c>icacls.exe</c>). Unit tests inject a
/// stub to assert on the full argument list without actually touching the host.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Run a process synchronously and return exit code + captured stdout/stderr.</summary>
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>Result of a single <see cref="IProcessRunner.Run"/> call.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

internal sealed class DefaultProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Process '{fileName}' konnte nicht gestartet werden.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }
}
