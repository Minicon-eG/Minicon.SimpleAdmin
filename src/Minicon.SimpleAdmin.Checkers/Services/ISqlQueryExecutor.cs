namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Executes a single SQL query and returns all rows as a list of column-name-to-value maps.
/// Abstracts SqlConnection and SqlCommand to allow unit testing without a real SQL Server.
/// </summary>
public interface ISqlQueryExecutor
{
    /// <summary>
    /// Executes <paramref name="sql"/> against <paramref name="connectionString"/> and returns all rows.
    /// Column names are case-insensitive keys. Throws on connection failure or SQL execution error.
    /// </summary>
    Task<List<Dictionary<string, object?>>> ExecuteAsync(
        string connectionString,
        string sql,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}
