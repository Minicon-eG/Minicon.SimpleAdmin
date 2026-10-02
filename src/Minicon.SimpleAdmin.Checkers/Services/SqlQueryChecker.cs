using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Executes configured SQL query checks and evaluates assertions against the result sets.
/// </summary>
public sealed class SqlQueryChecker(
    ILogger<SqlQueryChecker> logger,
    ISqlQueryExecutor executor,
    string masterKey = "")
{
    private const int MaxStoredRows = 100;

    /// <summary>
    /// Executes all configured SQL query check groups in parallel and returns their states.
    /// </summary>
    public async Task<Dictionary<string, SqlQueryCheckState>> CheckAllAsync(
        IEnumerable<SqlQueryCheckConfig> checks,
        int defaultTimeoutSeconds)
    {
        var tasks = checks
            .Where(c => !string.IsNullOrWhiteSpace(c.ConnectionString))
            .Select(c => CheckGroupAsync(c, defaultTimeoutSeconds));

        var results = await Task.WhenAll(tasks);

        return results.ToDictionary(r => r.CheckId, r => r);
    }

    private async Task<SqlQueryCheckState> CheckGroupAsync(
        SqlQueryCheckConfig check,
        int defaultTimeoutSeconds)
    {
        var timeout = check.TimeoutSeconds ?? defaultTimeoutSeconds;
        var state = new SqlQueryCheckState
        {
            CheckId = check.Name,
            CheckName = check.Description,
            LastChecked = DateTime.UtcNow
        };

        string connStr;
        if (!string.IsNullOrEmpty(masterKey))
        {
            connStr = ConnectionStringEncryption.Decrypt(check.ConnectionString, masterKey);
        }
        else if (ConnectionStringEncryption.IsEncrypted(check.ConnectionString))
        {
            state.ConnectionError = "Encryption key not configured — cannot decrypt connection string";
            state.Status = MetricStatus.Critical;
            logger.LogWarning("SQL check '{CheckId}': connection string is encrypted but Encryption:ConnectionStringKey is not configured", check.Name);
            return state;
        }
        else
        {
            connStr = check.ConnectionString;
        }

        var rowSets = new List<List<Dictionary<string, object?>>>(check.Queries.Count);
        var hasConnected = false;
        var failedQueryNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in check.Queries)
        {
            var rows = new List<Dictionary<string, object?>>();
            try
            {
                rows = await executor.ExecuteAsync(connStr, query.Sql, timeout);
                hasConnected = true;
                rowSets.Add(rows);
                logger.LogDebug("SQL check '{CheckId}' query '{Query}': {RowCount} row(s)",
                    check.Name, query.Name, rows.Count);
            }
            catch (Exception ex)
            {
                rowSets.Add(rows);

                if (!hasConnected)
                {
                    state.ConnectionError = ex.Message;
                    state.Status = MetricStatus.Critical;
                    logger.LogWarning(ex, "SQL check '{CheckId}' connection failed", check.Name);
                    return state;
                }

                failedQueryNames.Add(query.Name);
                var queryResult = new SqlQueryResult
                {
                    QueryName = query.Name,
                    Status = MetricStatus.Critical,
                    ExecutionError = ex.Message
                };
                state.Results.Add(queryResult);
                logger.LogWarning(ex, "SQL check '{CheckId}' query '{Query}' execution failed", check.Name, query.Name);
            }
        }

        // Evaluate assertions for each query that executed successfully
        for (var i = 0; i < check.Queries.Count; i++)
        {
            if (failedQueryNames.Contains(check.Queries[i].Name))
                continue;

            var query = check.Queries[i];
            var rows = i < rowSets.Count ? rowSets[i] : new List<Dictionary<string, object?>>();

            var queryResult = EvaluateQuery(query, rows);
            state.Results.Add(queryResult);
        }

        state.Status = state.Results.Count > 0
            ? state.Results.Max(r => r.Status)
            : MetricStatus.Healthy;

        return state;
    }

    internal static SqlQueryResult EvaluateQuery(
        SqlQueryConfig query,
        List<Dictionary<string, object?>> rows)
    {
        var result = new SqlQueryResult
        {
            QueryName = query.Name,
            Status = MetricStatus.Healthy
        };

        // Row count assertion
        if (query.RowCount != null)
        {
            var rowCountResult = EvaluateRowCount(rows.Count, query.RowCount);
            result.RowCount = rowCountResult;
            if (!rowCountResult.Passed)
                result.Status = MetricStatus.Critical;
        }

        // Column assertions
        foreach (var assertion in query.ColumnAssertions)
        {
            var colResult = EvaluateColumnAssertion(assertion, rows);
            result.ColumnResults.Add(colResult);
            if (!colResult.Passed && result.Status < MetricStatus.Critical)
                result.Status = MetricStatus.Critical;
        }

        result.TotalRows = rows.Count;

        // Rows nur bei Fehler/Critical mit-speichern (sonst leer lassen, um JSON-Groesse zu schonen)
        if (result.Status != MetricStatus.Healthy && rows.Count > 0)
        {
            AttachRowPreview(result, rows);
        }

        return result;
    }

    private static void AttachRowPreview(SqlQueryResult result, List<Dictionary<string, object?>> rows)
    {
        result.Columns = rows[0].Keys.ToList();

        var take = Math.Min(rows.Count, MaxStoredRows);
        result.Rows = new List<List<string?>>(take);
        for (var i = 0; i < take; i++)
        {
            var row = rows[i];
            var cells = new List<string?>(result.Columns.Count);
            foreach (var col in result.Columns)
            {
                row.TryGetValue(col, out var value);
                cells.Add(FormatCell(value));
            }
            result.Rows.Add(cells);
        }
    }

    private static string? FormatCell(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (value is byte[] bytes) return $"0x{Convert.ToHexString(bytes)}";
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static SqlRowCountResult EvaluateRowCount(int actual, SqlRowCountAssertion assertion)
    {
        var passed = EvaluateOperator(actual, assertion.ExpectedCount, assertion.Operator);
        return new SqlRowCountResult
        {
            ActualCount = actual,
            ExpectedCount = assertion.ExpectedCount,
            Operator = assertion.Operator,
            Passed = passed
        };
    }

    internal static SqlColumnResult EvaluateColumnAssertion(
        SqlColumnAssertion assertion,
        List<Dictionary<string, object?>> rows)
    {
        var result = new SqlColumnResult
        {
            AssertionName = assertion.Name,
            Column = assertion.Column,
            ExpectedValue = assertion.ExpectedValue,
            Operator = assertion.Operator
        };

        if (rows.Count == 0)
        {
            result.Passed = true;
            result.ActualValue = "0 rows";
            return result;
        }

        var failures = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.TryGetValue(assertion.Column, out var rawValue))
            {
                failures.Add($"Row {i}: column '{assertion.Column}' not found");
                continue;
            }

            var actualStr = rawValue?.ToString() ?? string.Empty;
            try
            {
                if (!EvaluateSingleValue(actualStr, rawValue, assertion, out var reason))
                    failures.Add($"Row {i}='{actualStr}'" + (reason != null ? $" ({reason})" : ""));
            }
            catch (Exception ex)
            {
                failures.Add($"Row {i}: {ex.Message}");
            }
        }

        result.Passed = failures.Count == 0;
        result.ActualValue = result.Passed
            ? $"{rows.Count} row{(rows.Count == 1 ? "" : "s")} OK"
            : $"{failures.Count}/{rows.Count} rows failed";
        if (!result.Passed)
            result.FailureReason = string.Join("; ", failures);

        return result;
    }

    private static bool EvaluateSingleValue(
        string actualStr,
        object? rawValue,
        SqlColumnAssertion assertion,
        out string? reason)
    {
        return assertion.ValueType switch
        {
            SqlValueType.Integer => EvaluateInteger(actualStr, assertion.ExpectedValue, assertion.Operator, out reason),
            SqlValueType.DateTime => EvaluateDateTime(actualStr, rawValue, assertion.ExpectedValue, assertion.Operator, out reason),
            _ => EvaluateString(actualStr, assertion.ExpectedValue, assertion.Operator, out reason)
        };
    }

    internal static bool EvaluateString(string actual, string expected, string op, out string? reason)
    {
        reason = null;
        if (op is ">" or ">=" or "<" or "<=")
        {
            reason = $"Operator '{op}' is not supported for String type (use == or !=)";
            return false;
        }

        var match = op switch
        {
            "==" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            _ => throw new InvalidOperationException($"Unknown operator '{op}'")
        };

        if (!match)
            reason = $"Expected '{expected}', got '{actual}'";

        return match;
    }

    internal static bool EvaluateInteger(string actualStr, string expectedStr, string op, out string? reason)
    {
        reason = null;

        if (!long.TryParse(actualStr, out var actual))
        {
            reason = $"Could not parse actual value '{actualStr}' as Integer";
            return false;
        }

        if (!long.TryParse(expectedStr, out var expected))
        {
            reason = $"Could not parse expected value '{expectedStr}' as Integer";
            return false;
        }

        var passed = EvaluateOperator(actual, expected, op);
        if (!passed)
            reason = $"Expected {op} {expected}, got {actual}";

        return passed;
    }

    internal static bool EvaluateDateTime(
        string actualStr,
        object? rawValue,
        string expectedStr,
        string op,
        out string? reason)
    {
        reason = null;

        System.DateTime actual;
        if (rawValue is System.DateTime dt)
        {
            actual = dt;
        }
        else if (!System.DateTime.TryParse(actualStr, out actual))
        {
            reason = $"Could not parse actual value '{actualStr}' as DateTime";
            return false;
        }

        var expected = ParseExpectedDateTime(expectedStr, out var parseError);
        if (expected == null)
        {
            reason = parseError ?? $"Could not parse expected value '{expectedStr}' as DateTime";
            return false;
        }

        var passed = EvaluateOperator(actual.Ticks, expected.Value.Ticks, op);
        if (!passed)
            reason = $"Expected {op} {expected.Value:O}, got {actual:O}";

        return passed;
    }

    internal static System.DateTime? ParseExpectedDateTime(string value, out string? error)
    {
        error = null;
        var trimmed = value.Trim();

        if (trimmed.Equals("now", StringComparison.OrdinalIgnoreCase))
            return System.DateTime.UtcNow;

        if (trimmed.Equals("today", StringComparison.OrdinalIgnoreCase))
            return System.DateTime.UtcNow.Date;

        // Relative: now±Nunit  (e.g. now-30m, now+1h, now-1d)
        if (trimmed.StartsWith("now", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 3)
        {
            var rest = trimmed[3..];
            if (rest.Length >= 2)
            {
                var sign = rest[0] == '-' ? -1 : 1;
                var spec = rest[1..];
                var unit = spec[^1];
                if (int.TryParse(spec[..^1], out var amount))
                {
                    return unit switch
                    {
                        'm' => System.DateTime.UtcNow.AddMinutes(sign * amount),
                        'h' => System.DateTime.UtcNow.AddHours(sign * amount),
                        'd' => System.DateTime.UtcNow.AddDays(sign * amount),
                        _ => null
                    };
                }
            }

            error = $"Unrecognised relative DateTime token '{value}'. Use: now, today, now±Nm, now±Nh, now±Nd";
            return null;
        }

        if (System.DateTime.TryParse(trimmed, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        error = $"Could not parse '{value}' as DateTime. Use ISO 8601 or a relative token (now, today, now-1h)";
        return null;
    }

    internal static bool EvaluateOperator<T>(T actual, T expected, string op) where T : IComparable<T>
    {
        return op switch
        {
            "==" => actual.CompareTo(expected) == 0,
            "!=" => actual.CompareTo(expected) != 0,
            ">" => actual.CompareTo(expected) > 0,
            ">=" => actual.CompareTo(expected) >= 0,
            "<" => actual.CompareTo(expected) < 0,
            "<=" => actual.CompareTo(expected) <= 0,
            _ => throw new InvalidOperationException($"Unknown operator '{op}'")
        };
    }
}
