namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Acknowledge state - root object for acknowledges.json
/// </summary>
public class AcknowledgeState
{
    /// <summary>
    /// Timestamp of last update
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Active acknowledges
    /// </summary>
    public List<Acknowledge> Acknowledges { get; set; } = new();

    /// <summary>
    /// Historical acknowledges (resolved/expired)
    /// </summary>
    public List<Acknowledge> History { get; set; } = new();

    /// <summary>
    /// Gets an acknowledge by ID
    /// </summary>
    public Acknowledge? GetById(string acknowledgeId)
    {
        return Acknowledges.FirstOrDefault(a => a.Id == acknowledgeId);
    }

    /// <summary>
    /// Gets all active acknowledges for a server
    /// </summary>
    public IEnumerable<Acknowledge> GetByServerId(string serverId)
    {
        return Acknowledges.Where(a => a.ServerId == serverId && a.Status == AcknowledgeStatus.Active);
    }

    /// <summary>
    /// Gets an active acknowledge for a specific problem
    /// </summary>
    public Acknowledge? GetByProblemId(string serverId, string problemId)
    {
        return Acknowledges.FirstOrDefault(a =>
            a.ServerId == serverId &&
            a.ProblemId == problemId &&
            a.Status == AcknowledgeStatus.Active);
    }

    /// <summary>
    /// Adds a new acknowledge
    /// </summary>
    public void Add(Acknowledge acknowledge)
    {
        Acknowledges.Add(acknowledge);
        LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// Moves an acknowledge to history
    /// </summary>
    public void MoveToHistory(string acknowledgeId)
    {
        var ack = Acknowledges.FirstOrDefault(a => a.Id == acknowledgeId);
        if (ack != null)
        {
            Acknowledges.Remove(ack);
            History.Insert(0, ack);
            LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Cleans up old history entries based on retention days
    /// </summary>
    public int CleanupHistory(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var removed = History.RemoveAll(a => a.AcknowledgedAt < cutoff);
        if (removed > 0)
        {
            LastUpdated = DateTime.UtcNow;
        }
        return removed;
    }

    /// <summary>
    /// Processes expired acknowledges
    /// </summary>
    /// <returns>List of expired acknowledge IDs</returns>
    public List<string> ProcessExpired()
    {
        var expired = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var ack in Acknowledges.Where(a => a.Status == AcknowledgeStatus.Active && a.ExpiresAt.HasValue && a.ExpiresAt <= now).ToList())
        {
            ack.Status = AcknowledgeStatus.Expired;
            expired.Add(ack.Id);
            MoveToHistory(ack.Id);
        }

        return expired;
    }
}
