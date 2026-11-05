using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// Audit log of the scheduled-task control (<c>scheduled-task-actions.json</c>). Every append re-reads the
/// file and merges under a <c>.lock</c> file (same pattern as the script runner's history.json), so two WebUI
/// processes during an IIS overlapped recycle do not overwrite each other. Written atomically (temp file +
/// <see cref="File.Move(string, string, bool)"/>), newest first, capped. Entries that could not be written
/// (IO error, lock not obtainable) stay pending in memory and are written with the next append — the file is
/// never written without holding the lock.
/// </summary>
internal sealed class ScheduledTaskAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly int _maxEntries;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly List<ScheduledTaskAuditEntry> _pending = new();

    /// <summary>Attempts × delay for the cross-process file lock (default 30 × 100 ms).</summary>
    internal int LockAttempts { get; set; } = 30;

    public ScheduledTaskAuditLog(string directory, int maxEntries, ILogger logger)
    {
        _directory = directory;
        _maxEntries = Math.Max(10, maxEntries);
        _logger = logger;
    }

    internal string FilePath => Path.Combine(_directory, ScheduledTaskControlOptions.AuditFileName);

    /// <summary>Appends the entries. Returns null on success, otherwise the error text (entries stay pending).</summary>
    public async Task<string?> AppendAsync(IReadOnlyCollection<ScheduledTaskAuditEntry> entries)
    {
        lock (_pendingLock)
        {
            _pending.AddRange(entries);
            if (_pending.Count > _maxEntries)
            {
                var dropped = _pending.Count - _maxEntries;
                _pending.RemoveRange(0, dropped);
                _logger.LogError("Scheduled task audit log {Path} has not been writable for a while: {Count} oldest pending entries were discarded", FilePath, dropped);
            }
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            List<ScheduledTaskAuditEntry> snapshot;
            lock (_pendingLock) snapshot = _pending.ToList();
            if (snapshot.Count == 0) return null; // written by a concurrent append

            Directory.CreateDirectory(_directory);
            await using var fileLock = await AcquireFileLockAsync().ConfigureAwait(false);
            if (fileLock == null)
                return "Die Sperrdatei des Audit-Logs konnte nicht belegt werden.";

            var merged = Merge(await ReadFileAsync(moveCorruptAside: true).ConfigureAwait(false), snapshot);
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(merged, JsonOptions)).ConfigureAwait(false);
            File.Move(tmp, FilePath, overwrite: true);

            lock (_pendingLock)
                _pending.RemoveAll(e => snapshot.Contains(e));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write scheduled task audit log {Path}", FilePath);
            try { File.Delete(tmp); } catch { /* best effort */ }
            return ex.Message;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Newest entries first (file + not yet written entries). The file is replaced atomically, so no lock is needed.</summary>
    public IReadOnlyList<ScheduledTaskAuditEntry> Read(int max, string? taskPath)
    {
        List<ScheduledTaskAuditEntry> disk;
        try
        {
            disk = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ScheduledTaskAuditEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read scheduled task audit log {Path}", FilePath);
            disk = new List<ScheduledTaskAuditEntry>();
        }

        List<ScheduledTaskAuditEntry> pending;
        lock (_pendingLock) pending = _pending.ToList();

        IEnumerable<ScheduledTaskAuditEntry> query = Merge(disk, pending);
        if (!string.IsNullOrWhiteSpace(taskPath))
            query = query.Where(e => string.Equals(e.TaskPath, taskPath, StringComparison.OrdinalIgnoreCase));
        return query.Take(Math.Max(1, max)).ToList();
    }

    private List<ScheduledTaskAuditEntry> Merge(IEnumerable<ScheduledTaskAuditEntry> disk, IEnumerable<ScheduledTaskAuditEntry> own)
    {
        // Entries have no ID; the full content (timestamp with ticks) identifies them.
        var byKey = new Dictionary<string, ScheduledTaskAuditEntry>(StringComparer.Ordinal);
        foreach (var e in disk.Concat(own))
            byKey.TryAdd(Key(e), e);

        return byKey.Values
            .OrderByDescending(e => e.Timestamp)
            .Take(_maxEntries)
            .ToList();

        static string Key(ScheduledTaskAuditEntry e) =>
            string.Join('\u001f', e.Timestamp.ToUniversalTime().Ticks, e.User, e.Server, e.TaskPath, e.Action, e.Success, e.Message);
    }

    private async Task<List<ScheduledTaskAuditEntry>> ReadFileAsync(bool moveCorruptAside)
    {
        if (!File.Exists(FilePath)) return new List<ScheduledTaskAuditEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<ScheduledTaskAuditEntry>>(await File.ReadAllTextAsync(FilePath).ConfigureAwait(false), JsonOptions)
                   ?? new List<ScheduledTaskAuditEntry>();
        }
        catch (JsonException ex) when (moveCorruptAside)
        {
            // Never overwrite an unreadable audit log — keep it for inspection and start a new one.
            var aside = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(FilePath, aside, overwrite: true);
            _logger.LogError(ex, "Scheduled task audit log {Path} was unreadable and has been moved to {Aside}", FilePath, aside);
            return new List<ScheduledTaskAuditEntry>();
        }
    }

    /// <summary>Cross-process lock for the audit file; null when it cannot be obtained (then nothing is written).</summary>
    private async Task<FileStream?> AcquireFileLockAsync()
    {
        var path = FilePath + ".lock";
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        _logger.LogWarning("Could not lock {Path}; audit entries stay pending until the next write", path);
        return null;
    }
}
