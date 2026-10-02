namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Production implementation of <see cref="IEnvironmentService"/> using <see cref="System.Environment"/>.
/// </summary>
public class SystemEnvironmentService : IEnvironmentService
{
    /// <inheritdoc />
    public void Exit(int exitCode) => Environment.Exit(exitCode);

    /// <inheritdoc />
    public string MachineName => Environment.MachineName;
}
