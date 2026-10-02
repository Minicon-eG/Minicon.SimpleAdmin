namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// State of a single Windows Service
/// </summary>
public class ServiceState
{
    /// <summary>
    /// Service name (e.g. "W3SVC")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the service
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Current service status (Running, Stopped, Starting, Stopping, Unknown, NotFound, Error)
    /// </summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// Whether the service is considered healthy (running)
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Windows startup type: "Automatic", "Manual", or null when unknown / not found
    /// </summary>
    public string? StartupType { get; set; }
}

/// <summary>
/// State of a single certificate
/// </summary>
public class CertificateState
{
    /// <summary>
    /// Certificate subject
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Certificate thumbprint
    /// </summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Certificate expiration date
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Days until the certificate expires (negative if already expired)
    /// </summary>
    public int DaysUntilExpiry { get; set; }

    /// <summary>
    /// Status based on expiry thresholds
    /// </summary>
    public MetricStatus Status { get; set; } = MetricStatus.Unknown;
}

/// <summary>
/// State of event log checks (aggregated across all configured logs)
/// </summary>
public class EventLogState
{
    /// <summary>
    /// Timestamp of the last check
    /// </summary>
    public DateTime LastChecked { get; set; }

    /// <summary>
    /// Total number of matching events found (after ignore rules were applied)
    /// </summary>
    public int EventCount { get; set; }

    /// <summary>
    /// Number of events suppressed by ignore rules (not counted against thresholds)
    /// </summary>
    public int IgnoredCount { get; set; }

    /// <summary>
    /// Overall status based on event counts and thresholds
    /// </summary>
    public MetricStatus Status { get; set; } = MetricStatus.Healthy;

    /// <summary>
    /// Recent event log entries (limited, sorted by timestamp descending)
    /// </summary>
    public List<EventLogEntry> RecentEvents { get; set; } = new();
}

/// <summary>
/// Aggregated result of all queries in one SQL check group
/// </summary>
public class SqlQueryCheckState
{
    /// <summary>Check group identifier</summary>
    public string CheckId { get; set; } = string.Empty;

    /// <summary>Check group display name</summary>
    public string CheckName { get; set; } = string.Empty;

    /// <summary>When the check was last executed</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>Worst status across all queries in this group</summary>
    public MetricStatus Status { get; set; }

    /// <summary>Set when the database connection itself failed</summary>
    public string? ConnectionError { get; set; }

    /// <summary>Individual query results</summary>
    public List<SqlQueryResult> Results { get; set; } = new();
}

/// <summary>
/// Result of executing a single SQL query with all its assertions
/// </summary>
public class SqlQueryResult
{
    /// <summary>Query display name</summary>
    public string QueryName { get; set; } = string.Empty;

    /// <summary>Worst status across all assertions for this query</summary>
    public MetricStatus Status { get; set; }

    /// <summary>Set when the SQL execution itself failed (syntax error, timeout, etc.)</summary>
    public string? ExecutionError { get; set; }

    /// <summary>Row-count assertion result (null when no RowCount assertion is configured)</summary>
    public SqlRowCountResult? RowCount { get; set; }

    /// <summary>Column assertion results — one entry per configured SqlColumnAssertion</summary>
    public List<SqlColumnResult> ColumnResults { get; set; } = new();

    /// <summary>Spaltennamen der zurueckgegebenen Zeilen (nur gesetzt bei Fehler/Critical-Status)</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>Erste N Zeilen der Query (nur gesetzt bei Fehler/Critical-Status, max. 100)</summary>
    public List<List<string?>> Rows { get; set; } = new();

    /// <summary>Gesamtzahl der Zeilen vor dem Trimmen auf 100</summary>
    public int TotalRows { get; set; }
}

/// <summary>
/// Result of a row-count assertion
/// </summary>
public class SqlRowCountResult
{
    /// <summary>Number of rows the query actually returned</summary>
    public int ActualCount { get; set; }

    /// <summary>Expected count from configuration</summary>
    public int ExpectedCount { get; set; }

    /// <summary>Configured comparison operator</summary>
    public string Operator { get; set; } = "==";

    /// <summary>True when the assertion passed</summary>
    public bool Passed { get; set; }
}

/// <summary>
/// Result of a single column-value assertion evaluated across all rows
/// </summary>
public class SqlColumnResult
{
    /// <summary>Assertion display name</summary>
    public string AssertionName { get; set; } = string.Empty;

    /// <summary>Column name that was checked</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>Summary of checked rows, e.g. "5 rows OK" or "2/5 rows failed"</summary>
    public string? ActualValue { get; set; }

    /// <summary>Expected value from configuration</summary>
    public string ExpectedValue { get; set; } = string.Empty;

    /// <summary>Configured comparison operator</summary>
    public string Operator { get; set; } = "==";

    /// <summary>True when all rows satisfied the assertion</summary>
    public bool Passed { get; set; }

    /// <summary>Details of failing rows when Passed is false, e.g. "Row 0='foo'; Row 2='bar'"</summary>
    public string? FailureReason { get; set; }
}

/// <summary>
/// State of a single email probe (sender role) — result of one SMTP send attempt
/// </summary>
public class EmailProbeState
{
    /// <summary>Probe slug identifier (matches EmailProbeConfig.Name)</summary>
    public string ProbeId { get; set; } = string.Empty;

    /// <summary>Human-readable probe name</summary>
    public string ProbeName { get; set; } = string.Empty;

    /// <summary>When this state was last evaluated</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>Healthy = last send succeeded; Critical = SMTP error occurred</summary>
    public MetricStatus Status { get; set; }

    /// <summary>When the most recent probe email was sent (null if never sent yet)</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>SMTP error message when Status is Critical</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of consecutive cycles in which this probe has been non-Healthy.
    /// Reset to 0 on the first Healthy result. Carried forward across cycles for status smoothing.
    /// </summary>
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// State of a single email delivery check (checker role) — result of one IMAP freshness check
/// </summary>
public class EmailDeliveryCheckState
{
    /// <summary>Check slug identifier (matches EmailDeliveryCheckConfig.Name)</summary>
    public string CheckId { get; set; } = string.Empty;

    /// <summary>Human-readable check name</summary>
    public string CheckName { get; set; } = string.Empty;

    /// <summary>When this state was last evaluated</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>Healthy / Warning / Critical based on message age thresholds</summary>
    public MetricStatus Status { get; set; }

    /// <summary>Date of the newest matching probe email found in the mailbox (null if none found)</summary>
    public DateTime? LastReceivedAt { get; set; }

    /// <summary>Age of the newest matching email in minutes (null if none found)</summary>
    public double? AgeMinutes { get; set; }

    /// <summary>
    /// Error message when the check could not complete (IMAP connection failure,
    /// authentication error, etc.) or when no matching messages were found
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// True when Status is Critical because the IMAP connection itself failed
    /// (as opposed to a search-level failure like "no messages found").
    /// Used by ProblemDerivationService to produce the correct problem ID.
    /// </summary>
    public bool IsConnectionError { get; set; }

    /// <summary>
    /// True when no messages matching the subject filter were found in the mailbox.
    /// Distinct from IsConnectionError (IMAP worked, mailbox is just empty).
    /// Used by ProblemDerivationService to produce a "not found" message instead of "N minutes stale".
    /// </summary>
    public bool IsNotFound { get; set; }

    /// <summary>
    /// Number of consecutive cycles in which this check has been non-Healthy.
    /// Reset to 0 on the first Healthy result. Carried forward across cycles for status smoothing.
    /// </summary>
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// A single event log entry
/// </summary>
public class EventLogEntry
{
    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Event level (Critical, Error, Warning, Information)
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// Event source (provider name)
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Event message text
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Windows Event ID
    /// </summary>
    public int EventId { get; set; }
}
