namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Per-notifier-instance state persisted as notify-state.json and published via HTTP so peer
/// notifier instances can deduplicate. Keyed by server ID and <see cref="ActiveProblem.Id"/>.
/// </summary>
public class NotifyState
{
    /// <summary>Identifier of the notifier instance that produced this state (e.g. machine name).</summary>
    public string GeneratedBy { get; set; } = string.Empty;

    /// <summary>When this state file was last written (UTC).</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Notification bookkeeping per server-scoped problem ID.</summary>
    public Dictionary<string, NotifyEntry> Entries { get; set; } = new();

    /// <summary>When a scheduled status report send was last attempted (claim - written BEFORE sending, UTC).</summary>
    public DateTime? LastReportAttemptAt { get; set; }

    /// <summary>When a scheduled status report was last sent successfully (UTC).</summary>
    public DateTime? LastReportSentAt { get; set; }
}

/// <summary>
/// Notification bookkeeping for a single problem (claim-first model, mirrors EmailProbeSender's
/// .lastattempt / .lastsent files).
/// </summary>
public class NotifyEntry
{
    /// <summary>Server the problem belongs to.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Stable problem ID used for acknowledgement deep-links.</summary>
    public string ProblemId { get; set; } = string.Empty;

    /// <summary>Problem severity at last notification.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Problem type at last notification (e.g. cpu, service, certificate) — used for an icon in the
    /// resolved/all-clear notice. May be empty for state written by an older notifier version.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable problem message at last notification. Persisted so the resolved/all-clear
    /// notice can state exactly which problem was cleared, not just "a problem". May be empty for
    /// state written by an older notifier version.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>When the problem was first seen by the notifier (UTC).</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>When a send was last attempted (claim - written BEFORE sending). Used for cross-instance dedup.</summary>
    public DateTime LastAttemptAt { get; set; }

    /// <summary>When a send last succeeded (UTC). Drives the reminder interval. Null = never successfully sent.</summary>
    public DateTime? LastNotifiedAt { get; set; }

    /// <summary>How many notifications have been sent for this problem.</summary>
    public int NotifyCount { get; set; }

    /// <summary>Whether a resolved/all-clear notice has already been sent for this problem.</summary>
    public bool ResolvedNoticeSent { get; set; }
}
