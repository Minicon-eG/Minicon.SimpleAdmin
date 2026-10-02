namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Represents a single acknowledge for a problem
/// </summary>
public class Acknowledge
{
    /// <summary>
    /// Unique acknowledge ID (e.g., "ack_20260203083000")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Server ID where the problem exists
    /// </summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Problem ID that is acknowledged
    /// </summary>
    public string ProblemId { get; set; } = string.Empty;

    /// <summary>
    /// Name of person who acknowledged
    /// </summary>
    public string AcknowledgedBy { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when acknowledged
    /// </summary>
    public DateTime AcknowledgedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expiration timestamp (null = indefinite)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// User comment/reason for acknowledge
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Acknowledge options
    /// </summary>
    public AcknowledgeOptions Options { get; set; } = new();

    /// <summary>
    /// Current status
    /// </summary>
    public AcknowledgeStatus Status { get; set; } = AcknowledgeStatus.Active;

    /// <summary>
    /// Timestamp when resolved (if resolved)
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Resolution message (if resolved)
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// Generates a new acknowledge ID
    /// </summary>
    public static string GenerateId()
    {
        return $"ack_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>
    /// Creates a new acknowledge
    /// </summary>
    public static Acknowledge Create(
        string serverId,
        string problemId,
        string acknowledgedBy,
        string comment,
        int durationMinutes,
        bool autoResetOnHealthy = true,
        bool suppressAlerts = true,
        DateTime? expiresAt = null)
    {
        // An explicit end date ("bis Datum") wins over the duration; durationMinutes = 0 without an
        // end date means indefinite (until cancelled manually).
        if (expiresAt.HasValue)
        {
            var utc = expiresAt.Value.Kind == DateTimeKind.Local ? expiresAt.Value.ToUniversalTime() : DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc);
            durationMinutes = Math.Max(1, (int)Math.Ceiling((utc - DateTime.UtcNow).TotalMinutes));
            var ack = Create(serverId, problemId, acknowledgedBy, comment, durationMinutes, autoResetOnHealthy, suppressAlerts);
            ack.ExpiresAt = utc;
            return ack;
        }

        return new Acknowledge
        {
            Id = GenerateId(),
            ServerId = serverId,
            ProblemId = problemId,
            AcknowledgedBy = acknowledgedBy,
            AcknowledgedAt = DateTime.UtcNow,
            ExpiresAt = durationMinutes > 0 ? DateTime.UtcNow.AddMinutes(durationMinutes) : null,
            Comment = comment,
            Options = new AcknowledgeOptions
            {
                AutoResetOnHealthy = autoResetOnHealthy,
                SuppressAlerts = suppressAlerts,
                DurationMinutes = durationMinutes
            },
            Status = AcknowledgeStatus.Active
        };
    }

    /// <summary>Active and not yet expired (an expired entry may still be "Active" until a worker processes it).</summary>
    public bool IsEffective(DateTime nowUtc) =>
        Status == AcknowledgeStatus.Active && (!ExpiresAt.HasValue || ExpiresAt.Value > nowUtc);

    /// <summary>
    /// Extends the acknowledge duration
    /// </summary>
    public void Extend(int additionalMinutes)
    {
        if (ExpiresAt.HasValue)
        {
            var baseTime = ExpiresAt.Value > DateTime.UtcNow ? ExpiresAt.Value : DateTime.UtcNow;
            ExpiresAt = baseTime.AddMinutes(additionalMinutes);
            Options.DurationMinutes += additionalMinutes;
        }
    }

    /// <summary>
    /// Resolves the acknowledge
    /// </summary>
    public void Resolve(string? resolution = null)
    {
        Status = AcknowledgeStatus.Resolved;
        ResolvedAt = DateTime.UtcNow;
        Resolution = resolution ?? "Problem resolved";
    }

    /// <summary>
    /// Cancels the acknowledge
    /// </summary>
    public void Cancel()
    {
        Status = AcknowledgeStatus.Cancelled;
        ResolvedAt = DateTime.UtcNow;
        Resolution = "Manually cancelled";
    }

    /// <summary>
    /// Gets the remaining time until expiration
    /// </summary>
    public TimeSpan? GetRemainingTime()
    {
        if (!ExpiresAt.HasValue)
        {
            return null;
        }

        var remaining = ExpiresAt.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Checks if the acknowledge is expired
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    /// <summary>
    /// Checks if the acknowledge is active
    /// </summary>
    public bool IsActive => Status == AcknowledgeStatus.Active && !IsExpired;
}

/// <summary>
/// Acknowledge options
/// </summary>
public class AcknowledgeOptions
{
    /// <summary>
    /// Whether to auto-reset when the problem is resolved (default: true)
    /// </summary>
    public bool AutoResetOnHealthy { get; set; } = true;

    /// <summary>
    /// Whether to suppress alerts for this problem (default: true)
    /// </summary>
    public bool SuppressAlerts { get; set; } = true;

    /// <summary>
    /// Original duration in minutes (0 = indefinite)
    /// </summary>
    public int DurationMinutes { get; set; }
}

/// <summary>
/// Acknowledge status enumeration
/// </summary>
public enum AcknowledgeStatus
{
    /// <summary>
    /// Acknowledge is active
    /// </summary>
    Active,

    /// <summary>
    /// Acknowledge expired (timeout)
    /// </summary>
    Expired,

    /// <summary>
    /// Problem was resolved
    /// </summary>
    Resolved,

    /// <summary>
    /// Acknowledge was manually cancelled
    /// </summary>
    Cancelled
}
