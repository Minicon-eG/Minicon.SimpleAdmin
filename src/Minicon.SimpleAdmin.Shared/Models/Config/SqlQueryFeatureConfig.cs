namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Global SQL query checks feature configuration
/// </summary>
public class SqlQueryFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: false - opt-in)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Default connection/query timeout in seconds (default: 30)
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Per-server SQL query checks container
/// </summary>
public class ServerSqlQueriesConfig
{
    /// <summary>
    /// Whether SQL query checks are enabled for this server
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// List of SQL query check groups for this server
    /// </summary>
    public List<SqlQueryCheckConfig> Checks { get; set; } = new();
}

/// <summary>
/// A single SQL query check group — one connection string with one or more queries
/// </summary>
public class SqlQueryCheckConfig
{
    /// <summary>
    /// Unique slug identifier within this server (e.g., "biztalkdb-jobs")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// SQL Server connection string
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Connection/query timeout in seconds — overrides the global default when set
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Queries to execute in this check group
    /// </summary>
    public List<SqlQueryConfig> Queries { get; set; } = new();
}

/// <summary>
/// A single SQL query with optional row-count and column-value assertions
/// </summary>
public class SqlQueryConfig
{
    /// <summary>
    /// Display name for this query
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SQL statement to execute (SELECT only recommended)
    /// </summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>
    /// Row-count assertion — checked if not null
    /// </summary>
    public SqlRowCountAssertion? RowCount { get; set; }

    /// <summary>
    /// Column-value assertions — each checks one cell in the result set
    /// </summary>
    public List<SqlColumnAssertion> ColumnAssertions { get; set; } = new();
}

/// <summary>
/// Asserts the number of rows returned by a query
/// </summary>
public class SqlRowCountAssertion
{
    /// <summary>
    /// Expected number of rows
    /// </summary>
    public int ExpectedCount { get; set; }

    /// <summary>
    /// Comparison operator: ==, !=, &gt;, &gt;=, &lt;, &lt;= (default: ==)
    /// </summary>
    public string Operator { get; set; } = "==";
}

/// <summary>
/// Asserts the value of a column across all rows in the query result set.
/// The check passes only when every row satisfies the condition.
/// </summary>
public class SqlColumnAssertion
{
    /// <summary>
    /// Display name for this assertion
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Column name in the result set
    /// </summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Expected value as string — parsed according to ValueType
    /// </summary>
    public string ExpectedValue { get; set; } = string.Empty;

    /// <summary>
    /// Comparison operator: ==, !=, &gt;, &gt;=, &lt;, &lt;= (default: ==)
    /// </summary>
    public string Operator { get; set; } = "==";

    /// <summary>
    /// How to interpret and compare the value (default: String)
    /// </summary>
    public SqlValueType ValueType { get; set; } = SqlValueType.String;
}

/// <summary>
/// Data type used when comparing column values
/// </summary>
public enum SqlValueType
{
    /// <summary>Case-insensitive string comparison; only == and != are supported</summary>
    String,

    /// <summary>Parsed as long integer; all comparison operators supported</summary>
    Integer,

    /// <summary>
    /// Parsed as DateTime; all comparison operators supported.
    /// Expected value: ISO 8601 absolute (e.g. "2026-01-01T00:00:00Z")
    /// or a relative token: now, today, now-30m, now-1h, now-1d, now+30m, now+1h, now+1d
    /// </summary>
    DateTime
}
