namespace Minicon.SimpleAdmin.Models.Config;

// ── Sender role ───────────────────────────────────────────────────────────────

/// <summary>
/// SMTP connection settings for sending probe emails
/// </summary>
public class SmtpConfig
{
    /// <summary>SMTP server hostname or IP</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP server port (default: 25)</summary>
    public int Port { get; set; } = 25;

    /// <summary>
    /// SSL/TLS mode: true/false for backward compatibility, or explicit values like "auto".
    /// Allowed values: "true", "false", "auto".
    /// </summary>
    public string? SslMode { get; set; }

    /// <summary>Whether to use SSL/TLS (default: false). Backward-compatible fallback when SslMode is not set.</summary>
    public bool UseSsl { get; set; } = false;

    /// <summary>Sender address (From header)</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Recipient address — the shared monitoring mailbox</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// SMTP username — may be plain text or encrypted (enc:v1:... format)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// SMTP password — may be plain text or encrypted (enc:v1:... format)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>SMTP connection timeout in seconds. Defaults to 30 s when null.</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// A single email probe configuration — one SMTP path to test
/// </summary>
public class EmailProbeConfig
{
    /// <summary>Unique slug identifier within this server (e.g., "seg1-relay")</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>SMTP connection and authentication settings</summary>
    public SmtpConfig Smtp { get; set; } = new();

    /// <summary>
    /// Email subject used to identify this probe's messages in the IMAP mailbox.
    /// Should be unique per probe to allow independent subject-filter matching by the checker.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// How often to send a probe email in minutes (default: 15)
    /// </summary>
    public int SendIntervalMinutes { get; set; } = 15;
}

/// <summary>
/// Per-server email probe sender configuration
/// </summary>
public class ServerEmailProbesConfig
{
    /// <summary>Whether email probe sending is enabled for this server (default: false)</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Email probes to send from this server</summary>
    public List<EmailProbeConfig> Probes { get; set; } = new();
}

// ── Checker role ──────────────────────────────────────────────────────────────

/// <summary>
/// IMAP connection settings for reading the monitoring mailbox
/// </summary>
public class ImapConfig
{
    /// <summary>IMAP server hostname or IP</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>IMAP server port (default: 993)</summary>
    public int Port { get; set; } = 993;

    /// <summary>
    /// SSL/TLS mode: true/false for backward compatibility, or explicit values like "auto".
    /// Allowed values: "true", "false", "auto".
    /// </summary>
    public string? SslMode { get; set; }

    /// <summary>Whether to use SSL/TLS (default: true). Backward-compatible fallback when SslMode is not set.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// IMAP username — may be plain text or encrypted (enc:v1:... format)
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// IMAP password — may be plain text or encrypted (enc:v1:... format)
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Mailbox folder to search (default: INBOX)</summary>
    public string Folder { get; set; } = "INBOX";

    /// <summary>IMAP connection timeout in seconds. Defaults to 30 s when null.</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// A single email delivery check — verifies that probe emails from one sender arrive on time
/// </summary>
public class EmailDeliveryCheckConfig
{
    /// <summary>Unique slug identifier within this server (e.g., "seg1-relay-check")</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Subject text to search for in the IMAP folder (case-insensitive substring match).
    /// Must match the Subject configured on the corresponding sender probe.
    /// </summary>
    public string SubjectFilter { get; set; } = string.Empty;

    /// <summary>
    /// Age of the newest matching message in minutes that triggers a Warning (default: 20)
    /// </summary>
    public int MaxAgeWarningMinutes { get; set; } = 20;

    /// <summary>
    /// Age of the newest matching message in minutes that triggers a Critical alert (default: 60)
    /// </summary>
    public int MaxAgeCriticalMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to delete matching probe messages from the mailbox after checking (default: true)
    /// </summary>
    public bool DeleteAfterCheck { get; set; } = true;
}

/// <summary>
/// Per-server email delivery checker configuration.
/// The checker connects to a shared IMAP mailbox and verifies that probe emails
/// from each configured sender (identified by subject filter) arrived on time.
/// One IMAP connection is opened per worker cycle and reused for all checks.
/// </summary>
public class ServerEmailDeliveryConfig
{
    /// <summary>Whether email delivery checking is enabled for this server (default: false)</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Shared IMAP connection — used for all EmailDeliveryCheckConfig entries on this server
    /// </summary>
    public ImapConfig Imap { get; set; } = new();

    /// <summary>Delivery checks to perform — one per configured sender probe</summary>
    public List<EmailDeliveryCheckConfig> Checks { get; set; } = new();
}
