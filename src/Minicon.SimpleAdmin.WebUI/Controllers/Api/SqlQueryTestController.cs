using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

[ApiController]
[Route("api/sqlquerytest")]
public sealed class SqlQueryTestController(ConfigurationService configService) : ControllerBase
{
    private const int MaxRows = 50;

    public record ConnectionRequest(string ConnectionString, int? TimeoutSeconds);
    public record QueryRequest(string ConnectionString, string Sql, int? TimeoutSeconds);

    /// <summary>
    /// Tests a SQL Server connection string
    /// </summary>
    [HttpPost("connection")]
    public async Task<IActionResult> TestConnection([FromBody] ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return Ok(new { success = false, error = "Connection string darf nicht leer sein." });

        try
        {
            var connStr = configService.DecryptConnectionString(request.ConnectionString);
            using var connection = new SqlConnection(connStr);
            await connection.OpenAsync();
            var serverVersion = connection.ServerVersion;
            return Ok(new { success = true, serverVersion });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Executes a SQL query and returns the first 50 rows as strings
    /// </summary>
    [HttpPost("query")]
    public async Task<IActionResult> ExecuteQuery([FromBody] QueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return Ok(new { success = false, error = "Connection string darf nicht leer sein.", rowCount = 0 });

        if (string.IsNullOrWhiteSpace(request.Sql))
            return Ok(new { success = false, error = "SQL darf nicht leer sein.", rowCount = 0 });

        try
        {
            var connStr = configService.DecryptConnectionString(request.ConnectionString);
            using var connection = new SqlConnection(connStr);
            await connection.OpenAsync();

            using var command = new SqlCommand(request.Sql, connection)
            {
                CommandTimeout = request.TimeoutSeconds ?? 30
            };

            using var reader = await command.ExecuteReaderAsync();

            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<List<string?>>();
            var rowCount = 0;

            while (await reader.ReadAsync())
            {
                rowCount++;
                if (rows.Count < MaxRows)
                {
                    var row = new List<string?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString());
                    rows.Add(row);
                }
            }

            return Ok(new { success = true, rowCount, columns, rows });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message, rowCount = 0 });
        }
    }
}
