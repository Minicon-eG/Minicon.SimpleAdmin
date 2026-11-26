using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Services.Control;

namespace Minicon.SimpleAdmin.WebUI.Services.Backup;

public sealed record SettingsBackupAuditEntry(
    DateTimeOffset TimestampUtc,
    string User,
    string Action,
    string FileName,
    string ManifestHash,
    string Outcome,
    string? Error = null);

public interface ISettingsBackupEventAudit
{
    void TryWrite(SettingsBackupAuditEntry entry);
}

public interface ISettingsBackupAuditLog
{
    Task<bool> AppendAsync(SettingsBackupAuditEntry entry);
}

internal sealed class SettingsBackupAuditLog(string directory, ILogger logger) : ISettingsBackupAuditLog
{
    private const int Capacity = 2000;
    private readonly AtomicAuditLog<SettingsBackupAuditEntry> store =
        new(Path.Combine(directory, "settings-backup-actions.json"), logger);

    public async Task<bool> AppendAsync(SettingsBackupAuditEntry entry)
    {
        var error = await store.UpdateAsync(disk => disk.Concat([entry])
            .OrderByDescending(e => e.TimestampUtc).Take(Capacity).ToList());
        return error == null;
    }
}
