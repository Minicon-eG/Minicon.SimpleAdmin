using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services.Control;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

public interface IBizTalkAuditLog
{
    Task<bool> AppendAsync(BizTalkAuditEntry entry);
    IReadOnlyList<BizTalkAuditEntry> Read(int max = 1000, string? server = null, Guid? instanceId = null);
}

internal sealed class BizTalkAuditLog(string directory, int capacity, ILogger logger) : IBizTalkAuditLog
{
    private readonly AtomicAuditLog<BizTalkAuditEntry> store = new(Path.Combine(directory, "biztalk-instance-actions.json"), logger);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<BizTalkAuditEntry> pending = [];
    private readonly HashSet<Guid> requested = [];
    internal int LockAttempts { get => store.LockAttempts; set => store.LockAttempts = value; }

    public async Task<bool> AppendAsync(BizTalkAuditEntry entry)
    {
        await gate.WaitAsync();
        try
        {
            // Drain failed completions before accepting any new operation. Never invent a Requested after a failed prewrite.
            if (pending.Count > 0)
            {
                if (await store.UpdateAsync(disk => Merge(disk, pending)) != null)
                {
                    RememberCompletion(entry);
                    return false;
                }
                foreach (var completed in pending) requested.Remove(completed.OperationId);
                pending.Clear();
            }
            var error = await store.UpdateAsync(disk => Merge(disk, [entry]));
            if (error != null) RememberCompletion(entry);
            else if (entry.Phase == "Requested") requested.Add(entry.OperationId);
            else requested.Remove(entry.OperationId);
            return error == null;
        }
        finally { gate.Release(); }
    }

    private void RememberCompletion(BizTalkAuditEntry entry)
    {
        // Retain every completion of an already started operation, even while another completion cannot drain.
        // Repeated standalone rejections during an outage must not grow RAM without bound.
        if (entry.Phase == "Completed" && (pending.Count == 0 || requested.Contains(entry.OperationId)) &&
            !pending.Any(e => e.OperationId == entry.OperationId)) pending.Add(entry);
    }

    private async Task<List<BizTalkAuditEntry>> Merge(List<BizTalkAuditEntry> disk, IEnumerable<BizTalkAuditEntry> added)
    {
        var incoming = added.ToList();
        var operations = disk.Concat(incoming).DistinctBy(e => (e.OperationId, e.Phase))
            .GroupBy(e => e.OperationId).OrderByDescending(g => g.Max(e => e.Timestamp)).ToList();
        var open = operations.Where(g => !g.Any(e => e.Phase == "Completed")).ToList();
        var limit = Math.Max(10, capacity);
        // Capacity gates only admission of NEW open operations. Existing open operations may exceed a
        // reduced limit, and every completion/rejection must still be durably recordable.
        var existing = disk.Select(e => e.OperationId).ToHashSet();
        if (open.Count > limit && incoming.Any(e => e.Phase == "Requested" && !existing.Contains(e.OperationId)))
            throw new IOException("Audit-Kapazität durch offene Aktionen ausgeschöpft.");
        var completed = operations.Where(g => g.Any(e => e.Phase == "Completed")).ToList();
        // Keep an independent completed-history window. Archive overflow BEFORE replacing the active file.
        // Crash/write failure between archive and main replacement can duplicate entries, never lose them.
        foreach (var chunk in completed.Skip(limit).Chunk(limit))
        {
            var entries = chunk.SelectMany(g => g).OrderByDescending(e => e.Timestamp).ToList();
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries, AtomicAuditLog<BizTalkAuditEntry>.JsonOptions)));
            var archive = Path.Combine(directory, $"biztalk-instance-actions.archive-{hash}.json");
            if (!File.Exists(archive)) await AtomicAuditLog<BizTalkAuditEntry>.WriteSnapshotAsync(archive, entries);
            else if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))) != hash)
                throw new IOException("Auditarchiv beschädigt; aktive Einträge bleiben erhalten.");
        }
        return open.Concat(completed.Take(limit)).SelectMany(g => g).OrderByDescending(e => e.Timestamp).ToList();
    }

    // Failed completions intentionally stay represented by durable Requested on reads, never as persisted Completed.
    public IReadOnlyList<BizTalkAuditEntry> Read(int max = 1000, string? server = null, Guid? instanceId = null)
    {
        max = Math.Clamp(max, 1, 1000);
        // Read active first, then archive names: a concurrent rotation publishes the archive first.
        // Keep at most max matching phases in memory across segments. Deduplicate crash-safe overlaps.
        var selected = new Dictionary<(Guid, string), BizTalkAuditEntry>();
        void Include(IEnumerable<BizTalkAuditEntry> entries)
        {
            foreach (var e in entries)
            {
                if ((server != null && !string.Equals(server, e.Server, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(server, e.RequestedServer, StringComparison.OrdinalIgnoreCase)) ||
                    (instanceId != null && e.InstanceId != instanceId)) continue;
                selected.TryAdd((e.OperationId, e.Phase), e);
                if (selected.Count > max)
                {
                    var oldest = selected.MinBy(pair => pair.Value.Timestamp);
                    selected.Remove(oldest.Key);
                }
            }
        }
        Include(store.Read());
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "biztalk-instance-actions.archive-*.json"))
                Include(new AtomicAuditLog<BizTalkAuditEntry>(file, logger).Read());
        return selected.Values.OrderByDescending(e => e.Timestamp).ToList();
    }
}
