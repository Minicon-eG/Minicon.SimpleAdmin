using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Production <see cref="ISqlQueryExecutor"/> that runs queries against SQL Server
/// via <see cref="SqlConnection"/> and <see cref="SqlCommand"/>.
/// </summary>
public sealed class DefaultSqlQueryExecutor(ILogger<DefaultSqlQueryExecutor> logger) : ISqlQueryExecutor
{
    /// <inheritdoc/>
    public async Task<List<Dictionary<string, object?>>> ExecuteAsync(
        string connectionString,
        string sql,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<Dictionary<string, object?>>();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        logger.LogDebug("SQL query returned {RowCount} row(s)", rows.Count);
        return rows;
    }
}
