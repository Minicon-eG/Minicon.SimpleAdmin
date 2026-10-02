namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Provides access to current date and time values.
/// This abstraction enables testable code by allowing time to be mocked in tests.
/// </summary>
public interface IDateTimeProvider
{
    /// <summary>
    /// Gets the current date and time in Coordinated Universal Time (UTC).
    /// </summary>
    DateTime UtcNow { get; }
}
