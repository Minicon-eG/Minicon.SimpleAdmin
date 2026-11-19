using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.WebUI.Services.Control;

/// <summary>Shared file lock, read/merge and durable atomic replacement. Policy stays with each audit schema.</summary>
internal sealed class AtomicAuditLog<T>(string path, ILogger logger, bool nullIsEmpty = false, bool detailedErrors = false)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    internal int LockAttempts { get; set; } = 30;
    internal string FilePath => path;
    internal List<T> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? (nullIsEmpty ? [] : throw new JsonException("Ungültige Auditdatei.")) : [];

    internal Task<string?> UpdateAsync(Func<List<T>, List<T>> update, bool recoverCorrupt = false) =>
        UpdateAsync(disk => Task.FromResult(update(disk)), recoverCorrupt);

    internal async Task<string?> UpdateAsync(Func<List<T>, Task<List<T>>> update, bool recoverCorrupt = false)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fileLock = await AcquireAsync().ConfigureAwait(false);
            if (fileLock == null) return "Die Sperrdatei des Audit-Logs konnte nicht belegt werden.";
            List<T> disk;
            try { disk = Read(); }
            catch (JsonException) when (recoverCorrupt)
            {
                var aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
                File.Move(path, aside);
                logger.LogError("Unreadable audit {Path} preserved at {Aside}", path, aside);
                disk = [];
            }
            var merged = await update(disk).ConfigureAwait(false);
            await WriteSnapshotAsync(path, merged).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write audit {Path}", path);
            return detailedErrors ? ex.Message : "Die Auditdatei konnte nicht dauerhaft geschrieben werden.";
        }
        finally
        {
            gate.Release();
        }
    }

    // Also used for immutable BizTalk archive segments, while the main audit's lock is held.
    internal static async Task WriteSnapshotAsync(string destination, IReadOnlyList<T> entries)
    {
        var tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, destination, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private async Task<FileStream?> AcquireAsync()
    {
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(100).ConfigureAwait(false); }
        }
        return null;
    }
}
