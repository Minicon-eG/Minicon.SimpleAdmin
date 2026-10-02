namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Production implementation of <see cref="IDateTimeProvider"/> using <see cref="DateTime.UtcNow"/>.
/// </summary>
public class SystemDateTimeProvider : IDateTimeProvider
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
