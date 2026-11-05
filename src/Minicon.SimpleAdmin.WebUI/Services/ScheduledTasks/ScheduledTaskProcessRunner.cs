using System.Diagnostics;
using System.Text;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>Result of one PowerShell call of the scheduled-task control.</summary>
internal sealed record ScheduledTaskProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Seam for the PowerShell process of <see cref="ScheduledTaskControlService"/>: unlike the installer's
/// synchronous <c>IProcessRunner</c> it has a timeout (the process is killed) and honours cancellation.
/// Tests inject a stub to assert on the generated script without starting anything.
/// </summary>
internal interface IScheduledTaskProcessRunner
{
    /// <summary>
    /// Runs the process, reading stdout and stderr concurrently. On timeout the process tree is killed and
    /// <see cref="ScheduledTaskProcessResult.TimedOut"/> is set; on cancellation it is killed and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ScheduledTaskProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

internal sealed class ScheduledTaskProcessRunner : IScheduledTaskProcessRunner
{
    /// <summary>How long to wait for stdout/stderr EOF after exit/kill (a grandchild may still hold the pipes); both pipes in parallel.</summary>
    internal static readonly TimeSpan StreamDrainGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for the killed process to exit.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);

    public async Task<ScheduledTaskProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        // No interactive input: a prompt (e.g. credential request) must fail instead of hanging until the timeout.
        try { process.StandardInput.Close(); } catch { /* already gone */ }

        // Both pipes are drained concurrently — a full stderr pipe must not block the stdout reader.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        using (var timeoutCts = new CancellationTokenSource(timeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                timedOut = true;
            }
        }

        // Worst case after the timeout: KillWait + StreamDrainGrace (both pipes drained in parallel).
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(StreamDrainGrace).ConfigureAwait(false);
        }
        catch
        {
            // A pipe is still held open or failed — take what is there.
        }
        var stdout = Completed(stdoutTask);
        var stderr = Completed(stderrTask);
        return new ScheduledTaskProcessResult(timedOut ? -1 : process.ExitCode, stdout, stderr, timedOut);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)KillWait.TotalMilliseconds);
        }
        catch
        {
            // Already exited or not killable — the result is reported as timed out / cancelled either way.
        }
    }

    private static string Completed(Task<string> reader) =>
        reader.IsCompletedSuccessfully ? reader.Result : string.Empty;

}
