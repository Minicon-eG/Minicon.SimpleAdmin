namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Central notification feature configuration.
/// When enabled, a worker whose hostname is listed in <see cref="NotifierServers"/> aggregates the
/// status of all servers (read via HTTP), derives unacknowledged problems and sends summary e-mails
/// with acknowledge deep-links — no <c>--notify</c> flag required. Designed for HA: list several
/// servers and they coordinate via per-instance notify-state.json files published over HTTP
/// (claim-first, no shared lock). All HTTP URLs are derived from the configured servers' BaseUrls;
/// the URL fields below are optional overrides only.
/// </summary>
public class NotificationsFeatureConfig
{
    /// <summary>Master toggle (default: false - opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Names of the servers that act as notifiers (send the mails). A worker runs the notifier cycle
    /// when its hostname matches one of these. List two (on different servers) for HA. Replaces the
    /// former <c>--notify</c> CLI flag.
    /// </summary>
    public List<string> NotifierServers { get; set; } = new();

    /// <summary>
    /// Name of the server hosting the central WebUI. Its BaseUrl supplies the acknowledge deep-link
    /// base and the acknowledges.json URL. Empty = the first <see cref="NotifierServers"/> entry.
    /// </summary>
    public string WebUiServer { get; set; } = string.Empty;

    /// <summary>Minimum severity that triggers a notification: "Warning" or "Critical" (default: Warning).</summary>
    public string MinSeverity { get; set; } = "Warning";

    /// <summary>Report a server as a Critical problem when its status is unreachable or stale (default: true).</summary>
    public bool NotifyOnStaleServer { get; set; } = true;

    /// <summary>Status older than this (minutes) counts as stale (default: 10).</summary>
    public int StaleThresholdMinutes { get; set; } = 10;

    /// <summary>Reminder interval (hours) for problems that remain open and unacknowledged (default: 4).</summary>
    public int ReNotifyIntervalHours { get; set; } = 4;

    /// <summary>Retry window (minutes) after a failed send before the next attempt (default: 10).</summary>
    public int RetryAfterMinutes { get; set; } = 10;

    /// <summary>Send an all-clear notice when a previously notified problem disappears or is acknowledged (default: true).</summary>
    public bool SendResolvedNotice { get; set; } = true;

    /// <summary>Optional override for the acknowledge deep-link base. Empty = derived from <see cref="WebUiServer"/>'s BaseUrl.</summary>
    public string WebUiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the published acknowledges.json URL.
    /// Empty = derived as "{WebUiServer.BaseUrl}/status/acknowledges.json".
    /// </summary>
    public string AcknowledgesUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the per-server status URLs. Empty = derived from each active server's
    /// BaseUrl as "{BaseUrl}/status/{name}.status.json".
    /// </summary>
    public List<string> StatusUrls { get; set; } = new();

    /// <summary>
    /// Optional override for the peer notify-state URLs. Empty = derived from the OTHER
    /// <see cref="NotifierServers"/> as "{BaseUrl}/status/notify-state.json".
    /// </summary>
    public List<string> PeerNotifyStateUrls { get; set; } = new();

    /// <summary>
    /// Local path where this instance writes its own notify-state.json. Should be inside the
    /// directory published via IIS (e.g. "c:\inetpub\...\status\notify-state.json") so peers can read it.
    /// When empty, defaults to {statusDirectory}/notify-state.json.
    /// </summary>
    public string LocalNotifyStatePath { get; set; } = string.Empty;

    /// <summary>Reference to the SMTP probe to reuse for sending, format "ServerId:ProbeName". Empty = first available.</summary>
    public string SmtpRef { get; set; } = string.Empty;

    /// <summary>Comma-separated recipient list (overrides the probe's "To" field).</summary>
    public string Recipients { get; set; } = string.Empty;

    /// <summary>Subject prefix (default: "[SimpleAdmin]").</summary>
    public string SubjectPrefix { get; set; } = "[SimpleAdmin]";

    /// <summary>
    /// Send notification mails as plain text only — no multipart/HTML part
    /// (default: true — plain text is the safe default because some mail content
    /// filters silently swallow multipart/HTML mails while accepting them at the
    /// SMTP level). Set to false to opt in to the rich HTML mail template.
    /// </summary>
    public bool PlainTextOnly { get; set; } = true;

    /// <summary>
    /// Scheduled status report: at fixed local times the notifier sends one overview mail listing every
    /// server with warnings or errors — or an explicit "alles OK" mail. Sent in addition to the
    /// event-driven problem mails; requires <see cref="Enabled"/>.
    /// </summary>
    public StatusReportConfig StatusReport { get; set; } = new();
}

/// <summary>Configuration of the scheduled status report mail (see <see cref="NotificationsFeatureConfig.StatusReport"/>).</summary>
public class StatusReportConfig
{
    /// <summary>Master toggle for the scheduled report (default: false - opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Local send times in "HH:mm" format, e.g. ["07:00", "13:00"]. An entry may also hold several
    /// times separated by comma/semicolon/space ("07:00, 13:00") — the WebUI posts them that way.
    /// Default: 07:00, 13:00 and 17:00 (used when the key is missing from config.json).
    /// </summary>
    public List<string> Times { get; set; } = new() { "07:00", "13:00", "17:00" };

    /// <summary>
    /// How long after a scheduled time a missed report is still sent (e.g. worker or SMTP down at
    /// 07:00). Later runs skip that slot instead of sending an outdated report (default: 60).
    /// </summary>
    public int CatchUpMinutes { get; set; } = 60;

    /// <summary>
    /// Plain text only for the report mail. Null = same as <see cref="NotificationsFeatureConfig.PlainTextOnly"/>.
    /// </summary>
    public bool? PlainTextOnly { get; set; }
}
