using System.IO;

namespace Minicon.SimpleAdmin.IO;

/// <summary>
/// Provides a cross-process lock for state file mutations.
/// </summary>
public static class StateFilesLock
{
    private const string LockFileName = ".simpleadmin.state.lock";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Acquires the state file lock for the given directory.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        string stateDirectory,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateDirectory);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var startedAt = DateTime.UtcNow;
        var lockFilePath = Path.Combine(stateDirectory, LockFileName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow - startedAt < effectiveTimeout)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow - startedAt < effectiveTimeout)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}
