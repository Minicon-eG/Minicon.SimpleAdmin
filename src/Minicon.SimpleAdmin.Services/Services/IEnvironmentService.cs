namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Abstraction over <see cref="System.Environment"/> for testability.
/// </summary>
public interface IEnvironmentService
{
    /// <summary>
    /// Terminates the current process with the specified exit code.
    /// </summary>
    /// <param name="exitCode">The exit code to return to the operating system.</param>
    void Exit(int exitCode);

    /// <summary>
    /// Gets the NetBIOS name of the local computer.
    /// </summary>
    string MachineName { get; }
}
