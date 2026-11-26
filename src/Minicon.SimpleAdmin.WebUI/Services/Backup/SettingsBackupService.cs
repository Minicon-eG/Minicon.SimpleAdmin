using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Models.Backup;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Backup;

internal sealed record SettingsBackupDownload(byte[] Content, string FileName, string ManifestHash);

public sealed class SettingsBackupService : Microsoft.Extensions.Hosting.BackgroundService
{
    private const int MaxPendingPreviews = 8;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private readonly ConfigurationService configuration;
    private readonly ScriptDefinitionStore scripts;
    private readonly ScriptPathPolicy scriptPaths;
    private readonly StatusReaderService statusReader;
    private readonly SettingsBackupOptions options;
    private readonly ISettingsBackupAuditLog audit;
    private readonly ISettingsBackupEventAudit events;
    private readonly ILogger<SettingsBackupService> logger;
    private readonly TimeProvider timeProvider;
    private readonly string backupDirectory;
    private readonly string previewDirectory;
    private readonly SemaphoreSlim restoreLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingPreview> pending = new(StringComparer.Ordinal);

    public SettingsBackupService(
        ConfigurationService configuration,
        ScriptDefinitionStore scripts,
        ScriptPathPolicy scriptPaths,
        StatusReaderService statusReader,
        SettingsBackupOptions options,
        ISettingsBackupAuditLog audit,
        ISettingsBackupEventAudit events,
        ILogger<SettingsBackupService> logger,
        TimeProvider timeProvider,
        string configDirectory,
        string? previewDirectory = null)
    {
        this.configuration = configuration;
        this.scripts = scripts;
        this.scriptPaths = scriptPaths;
        this.statusReader = statusReader;
        this.options = options;
        this.audit = audit;
        this.events = events;
        this.logger = logger;
        this.timeProvider = timeProvider;
        backupDirectory = Path.Combine(configDirectory, "backup");
        this.previewDirectory = previewDirectory ?? Path.Combine(backupDirectory, ".preview");
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        CleanupPending(removeAllOrphans: true);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                CleanupPending();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal SettingsBackupPageViewModel GetPage(SettingsBackupPreview? preview = null, SettingsBackupResult? result = null) => new()
    {
        RestoreEnabled = options.RestoreEnabled,
        RestoreDisabledReason = options.DisabledReason,
        InstalledVersion = ChangelogProvider.InstalledVersion,
        MaxUploadBytes = SettingsBackupArchive.MaxUploadBytes,
        Preview = preview,
        Result = result,
        AutomaticBackups = ListAutomaticBackups()
    };

    internal async Task<SettingsBackupDownload> CreateDownloadAsync(string user)
    {
        var fileName = DownloadFileName();
        try
        {
            var payload = await CaptureFullPayloadAsync();
            var bytes = SettingsBackupArchive.Build(payload, ChangelogProvider.InstalledVersion,
                timeProvider.GetUtcNow(), Environment.MachineName, user);
            var package = SettingsBackupArchive.Parse(bytes);
            await AuditAsync("Download", fileName, package.ManifestHash, "Succeeded", user);
            return new SettingsBackupDownload(bytes, fileName, package.ManifestHash);
        }
        catch (Exception ex)
        {
            await AuditAsync("Download", fileName, "", "Failed", user, ex.Message);
            throw;
        }
    }

    internal async Task<SettingsBackupDownload> CreateAutomaticDownloadAsync(string fileName, string user)
    {
        try
        {
            var prepared = await ReadAutomaticAsync(fileName, user);
            await AuditAsync("Download", prepared.FileName, prepared.ManifestHash, "Succeeded", user);
            return prepared;
        }
        catch (Exception ex)
        {
            await AuditAsync("Download", fileName, "", "Failed", user, ex.Message);
            throw;
        }
    }

    internal async Task<SettingsBackupPreview> PreviewUploadAsync(Stream input, long declaredLength,
        string fileName, string user)
    {
        EnsureRestoreEnabled();
        if (declaredLength <= 0) throw new SettingsBackupException("Bitte eine Sicherungsdatei auswählen.");
        if (declaredLength > SettingsBackupArchive.MaxUploadBytes)
            throw new SettingsBackupException("Die Sicherungsdatei ist größer als 50 MB.");
        var bytes = await ReadUploadAsync(input);
        return await PreparePreviewAsync(bytes, SafeSourceName(fileName), user, requireFull: true);
    }

    internal async Task<SettingsBackupPreview> PreviewAutomaticAsync(string fileName, string user)
    {
        EnsureRestoreEnabled();
        var download = await ReadAutomaticAsync(fileName, user);
        return await PreparePreviewAsync(download.Content, fileName, user, requireFull: false);
    }

    internal async Task<SettingsBackupResult> RestoreAsync(string token, string user, bool removeUnlistedScripts = false)
    {
        token ??= string.Empty;
        pending.TryGetValue(token, out var preview);
        var sourceName = preview?.SourceFileName ?? "(unbekannt)";
        var manifestHash = preview?.ManifestHash ?? "";
        if (!options.RestoreEnabled)
        {
            var message = options.DisabledReason ?? "Das Einspielen ist deaktiviert.";
            await AuditAsync("Restore", sourceName, manifestHash, "Rejected", user, message);
            return new SettingsBackupResult { Message = message };
        }

        await restoreLock.WaitAsync();
        string? previousFileName = null;
        try
        {
            if (!pending.TryGetValue(token, out preview) || preview == null ||
                !string.Equals(preview.User, user, StringComparison.OrdinalIgnoreCase))
                throw new SettingsBackupException("Die Vorschau ist abgelaufen oder gehört zu einem anderen Benutzer. Bitte die Datei erneut hochladen.");
            if (preview.ExpiresAtUtc < timeProvider.GetUtcNow())
            {
                RemovePending(token);
                throw new SettingsBackupException("Die Vorschau ist abgelaufen oder gehört zu einem anderen Benutzer. Bitte die Datei erneut hochladen.");
            }

            var bytes = await File.ReadAllBytesAsync(preview.FilePath);
            var package = SettingsBackupArchive.Parse(bytes);
            if (preview.RequireFull && package.Manifest.BackupKind != "full")
                throw new SettingsBackupException("Nur vollständige Einstellungs-Sicherungen können hochgeladen werden.");
            manifestHash = package.ManifestHash;

            var previous = await CaptureFullPayloadAsync();
            previousFileName = SaveAutomaticFullBackup(previous, user);
            string[] rollbackFilesToRemove = package.Manifest.BackupKind == "full"
                ? package.ScriptFiles.Keys.Where(name => !previous.ScriptFiles.ContainsKey(name)).ToArray()
                : [];
            try
            {
                await ApplyAsync(package.Config, package.Scripts,
                    package.Manifest.BackupKind == "full" ? package.ScriptFiles : null,
                    removeUnlistedScripts ? preview.ScriptFilesToRemove : []);
            }
            catch (Exception restoreError)
            {
                logger.LogError(restoreError, "Settings restore failed; attempting rollback from {Backup}", previousFileName);
                try
                {
                    await ApplyAsync(previous.Config, previous.Scripts, previous.ScriptFiles, rollbackFilesToRemove);
                    await AuditAsync("Restore", sourceName, manifestHash, "Failed", user,
                        restoreError.Message + " Der vorherige Stand wurde wiederhergestellt.");
                    return new SettingsBackupResult
                    {
                        PreviousBackupFileName = previousFileName,
                        Message = "Das Einspielen ist fehlgeschlagen: " + restoreError.Message +
                                  " Der vorherige Stand wurde wiederhergestellt."
                    };
                }
                catch (Exception rollbackError)
                {
                    logger.LogCritical(rollbackError, "Settings restore rollback failed; use {Backup}", previousFileName);
                    await AuditAsync("Restore", sourceName, manifestHash, "Failed", user, restoreError.Message + " Rollback: " + rollbackError.Message);
                    return new SettingsBackupResult
                    {
                        PreviousBackupFileName = previousFileName,
                        Message = "Das Einspielen und die automatische Rücknahme sind fehlgeschlagen: " +
                                  restoreError.Message + " Bitte die herunterladbare vorherige Sicherung verwenden. Rollback: " +
                                  rollbackError.Message
                    };
                }
            }

            await AuditAsync("Restore", sourceName, manifestHash, "Succeeded", user);
            RemovePending(token);
            return new SettingsBackupResult
            {
                Success = true,
                PreviousBackupFileName = previousFileName,
                Message = "Die Einstellungen wurden erfolgreich eingespielt und neu geladen."
            };
        }
        catch (Exception ex)
        {
            await AuditAsync("Restore", sourceName, manifestHash, "Failed", user, ex.Message);
            return new SettingsBackupResult { PreviousBackupFileName = previousFileName, Message = ex.Message };
        }
        finally
        {
            restoreLock.Release();
        }
    }

    private async Task<SettingsBackupPreview> PreparePreviewAsync(byte[] bytes, string sourceName,
        string user, bool requireFull)
    {
        var package = SettingsBackupArchive.Parse(bytes);
        if (requireFull && package.Manifest.BackupKind != "full")
            throw new SettingsBackupException("Nur vollständige Einstellungs-Sicherungen können hochgeladen werden.");

        var currentConfig = await configuration.ReadCurrentFileAsync();
        var currentScripts = scripts.ReadCurrentFile();
        var currentFiles = ReadScriptFiles();
        var currentRoot = ParseNode(SettingsBackupArchive.DecodeUtf8(currentConfig));
        var backupRoot = package.Config == null ? null : ParseNode(SettingsBackupArchive.DecodeUtf8(package.Config));
        var currentScriptRoot = ParseNode(SettingsBackupArchive.DecodeUtf8(currentScripts));
        var backupScriptRoot = package.Scripts == null ? null : ParseNode(SettingsBackupArchive.DecodeUtf8(package.Scripts));
        var encrypted = EncryptedValues(backupRoot).ToList();
        var decryption = new Dictionary<string, bool>(StringComparer.Ordinal);
        var undecryptable = 0;
        foreach (var value in encrypted)
        {
            if (!decryption.TryGetValue(value, out var readable))
            {
                readable = CanDecrypt(value);
                decryption[value] = readable;
            }
            if (!readable) undecryptable++;
        }
        IReadOnlyList<string> scriptFilesToRemove = package.Manifest.BackupKind == "full"
            ? currentFiles.Keys.Except(package.ScriptFiles.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        CleanupPending(makeRoom: true);
        Directory.CreateDirectory(previewDirectory);
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var path = Path.Combine(previewDirectory, token + ".zip");
        await File.WriteAllBytesAsync(path, bytes);
        pending[token] = new PendingPreview(path, sourceName, package.ManifestHash, user,
            timeProvider.GetUtcNow().Add(PreviewLifetime), requireFull, scriptFilesToRemove);

        return new SettingsBackupPreview
        {
            Token = token,
            SourceFileName = sourceName,
            ManifestHash = package.ManifestHash,
            Manifest = package.Manifest,
            InstalledVersion = ChangelogProvider.InstalledVersion,
            NewerVersion = IsNewer(package.Manifest.SimpleAdminVersion, ChangelogProvider.InstalledVersion),
            EncryptedValues = encrypted.Count,
            UndecryptableValues = undecryptable,
            Servers = backupRoot == null ? NoDiff() : Diff(ConfigItems(currentRoot, "servers", "name"), ConfigItems(backupRoot, "servers", "name")),
            Environments = backupRoot == null ? NoDiff() : Diff(ConfigObject(currentRoot, "environments"), ConfigObject(backupRoot, "environments")),
            Domains = backupRoot == null ? NoDiff() : Diff(ConfigItems(currentRoot, "domains", "name"), ConfigItems(backupRoot, "domains", "name")),
            Scripts = backupScriptRoot == null ? NoDiff() : Diff(ConfigItems(currentScriptRoot, "scripts", "id", "name"), ConfigItems(backupScriptRoot, "scripts", "id", "name")),
            ScriptFiles = package.Manifest.BackupKind == "full" ? DiffBytes(currentFiles, package.ScriptFiles) : NoDiff(),
            ScriptFilesToRemove = scriptFilesToRemove
        };
    }

    private async Task ApplyAsync(byte[]? config, byte[]? scriptDefinitions,
        IReadOnlyDictionary<string, byte[]>? scriptFiles, IReadOnlyCollection<string> scriptFilesToRemove)
    {
        if (config != null) await configuration.ReplaceFileAsync(config);
        if (scriptDefinitions != null) scripts.ReplaceFile(scriptDefinitions);
        if (scriptFiles != null) ReplaceScriptFiles(scriptFiles, scriptFilesToRemove);
        await configuration.ReloadRestoredFileAsync();
        scripts.Reload();
        statusReader.InvalidateCache();
    }

    private async Task<SettingsBackupPayload> CaptureFullPayloadAsync()
    {
        var config = await configuration.ReadCurrentFileAsync();
        var scriptDefinitions = scripts.ReadCurrentFile();
        return new SettingsBackupPayload("full", config, scriptDefinitions, ReadScriptFiles());
    }

    private Dictionary<string, byte[]> ReadScriptFiles()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(scriptPaths.UploadDirectory)) return result;
        foreach (var path in Directory.EnumerateFiles(scriptPaths.UploadDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => SettingsBackupArchive.IsAllowedScript(Path.GetFileName(path)))
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            result.Add(Path.GetFileName(path), File.ReadAllBytes(path));
        return result;
    }

    private void ReplaceScriptFiles(IReadOnlyDictionary<string, byte[]> incoming,
        IReadOnlyCollection<string> scriptFilesToRemove)
    {
        Directory.CreateDirectory(scriptPaths.UploadDirectory);
        var staging = Path.Combine(scriptPaths.UploadDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in incoming)
                File.WriteAllBytes(Path.Combine(staging, file.Key), file.Value);
            foreach (var file in incoming)
                File.Move(Path.Combine(staging, file.Key), Path.Combine(scriptPaths.UploadDirectory, file.Key), overwrite: true);
            var wanted = incoming.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var confirmed = scriptFilesToRemove.Where(SettingsBackupArchive.IsAllowedScript).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(scriptPaths.UploadDirectory, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => SettingsBackupArchive.IsAllowedScript(Path.GetFileName(path))))
            {
                var fileName = Path.GetFileName(path);
                if (!wanted.Contains(fileName) && confirmed.Contains(fileName)) File.Delete(path);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private string SaveAutomaticFullBackup(SettingsBackupPayload payload, string user)
    {
        Directory.CreateDirectory(backupDirectory);
        var bytes = SettingsBackupArchive.Build(payload, ChangelogProvider.InstalledVersion,
            timeProvider.GetUtcNow(), Environment.MachineName, user);
        var baseName = Path.GetFileNameWithoutExtension(DownloadFileName());
        string fileName;
        string destination;
        var suffix = 0;
        do
        {
            fileName = baseName + (suffix == 0 ? "" : $"-{suffix:000}") + ".zip";
            destination = Path.Combine(backupDirectory, fileName);
            suffix++;
        } while (File.Exists(destination));
        var tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, destination);
            return fileName;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private async Task<SettingsBackupDownload> ReadAutomaticAsync(string fileName, string user)
    {
        var file = ResolveAutomatic(fileName);
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var content = await File.ReadAllBytesAsync(file.FullName);
            var zipPackage = SettingsBackupArchive.Parse(content);
            return new SettingsBackupDownload(content, fileName, zipPackage.ManifestHash);
        }

        var kind = fileName.StartsWith("config.json.", StringComparison.OrdinalIgnoreCase) ? "config" : "scripts";
        var raw = await File.ReadAllBytesAsync(file.FullName);
        var payload = new SettingsBackupPayload(kind, kind == "config" ? raw : null,
            kind == "scripts" ? raw : null, new Dictionary<string, byte[]>());
        var bytes = SettingsBackupArchive.Build(payload, ChangelogProvider.InstalledVersion,
            new DateTimeOffset(File.GetCreationTimeUtc(file.FullName), TimeSpan.Zero), Environment.MachineName, user);
        var package = SettingsBackupArchive.Parse(bytes);
        return new SettingsBackupDownload(bytes, fileName + ".zip", package.ManifestHash);
    }

    private FileInfo ResolveAutomatic(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !IsAutomaticFileName(fileName))
            throw new SettingsBackupException("Unzulässiger Dateiname der automatischen Sicherung.");
        var path = Path.Combine(backupDirectory, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("Die automatische Sicherung wurde nicht gefunden.", path);
        return new FileInfo(path);
    }

    private static bool IsAutomaticFileName(string fileName) =>
        (fileName.StartsWith("config.json.", StringComparison.OrdinalIgnoreCase) ||
         fileName.StartsWith("scripts.json.", StringComparison.OrdinalIgnoreCase)) && fileName.Length > 13 ||
        fileName.StartsWith("simpleadmin-einstellungen-", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private List<AutomaticSettingsBackup> ListAutomaticBackups()
    {
        if (!Directory.Exists(backupDirectory)) return [];
        return Directory.EnumerateFiles(backupDirectory).Select(path => new FileInfo(path))
            .Where(file => IsAutomaticFileName(file.Name))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(file => new AutomaticSettingsBackup
            {
                FileName = file.Name,
                CreatedAtUtc = file.CreationTimeUtc,
                Size = file.Length,
                Kind = file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "Vollständige Sicherung" :
                    file.Name.StartsWith("config.json.", StringComparison.OrdinalIgnoreCase) ? "config.json (F4)" : "scripts.json (F4)"
            }).ToList();
    }

    private string DownloadFileName()
    {
        var host = Environment.MachineName;
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':'])) host = host.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(host)) host = "unbekannt";
        if (host.Length > 80) host = host[..80];
        return $"simpleadmin-einstellungen-{host}-{timeProvider.GetLocalNow():yyyyMMdd-HHmmss}.zip";
    }

    private async Task AuditAsync(string action, string fileName, string manifestHash,
        string outcome, string user, string? error = null)
    {
        var entry = new SettingsBackupAuditEntry(timeProvider.GetUtcNow(), user, action,
            SafeSourceName(fileName), manifestHash, outcome, error?.Length > 2000 ? error[..2000] : error);
        if (!await audit.AppendAsync(entry)) logger.LogError("Settings backup file audit could not be persisted: {@AuditEntry}", entry);
        events.TryWrite(entry);
    }

    private bool CanDecrypt(string value)
    {
        if (!configuration.HasConnectionStringKey) return false;
        try { _ = configuration.DecryptConnectionString(value); return true; }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private void EnsureRestoreEnabled()
    {
        if (!options.RestoreEnabled)
            throw new SettingsBackupException(options.DisabledReason ?? "Das Einspielen ist deaktiviert.");
    }

    private static async Task<byte[]> ReadUploadAsync(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length + read > SettingsBackupArchive.MaxUploadBytes)
                throw new SettingsBackupException("Die Sicherungsdatei ist größer als 50 MB.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new SettingsBackupException("Bitte eine Sicherungsdatei auswählen.");
        return output.ToArray();
    }

    private static IEnumerable<string> EncryptedValues(JsonNode? root)
    {
        if (root is JsonObject obj)
        {
            foreach (var property in obj)
                foreach (var value in EncryptedValues(property.Value)) yield return value;
        }
        else if (root is JsonArray array)
        {
            foreach (var item in array)
                foreach (var value in EncryptedValues(item)) yield return value;
        }
        else if (root is JsonValue value && value.TryGetValue<string>(out var text) &&
                 text.StartsWith("enc:v1:", StringComparison.Ordinal))
            yield return text;
    }

    private static bool IsNewer(string backup, string installed)
    {
        var backupVersion = ParseVersion(backup);
        var installedVersion = ParseVersion(installed);
        return backupVersion != null && installedVersion != null && backupVersion > installedVersion;
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Trim().TrimStart('v', 'V').Split(['-', '+'])[0], out var version) ? version : null;

    private static SettingsBackupDiff NoDiff() => new();

    private static SettingsBackupDiff Diff(Dictionary<string, JsonNode?> current, Dictionary<string, JsonNode?> backup)
    {
        var result = new SettingsBackupDiff { Current = current.Count, Backup = backup.Count };
        foreach (var key in current.Keys.Union(backup.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!current.ContainsKey(key)) result.Added++;
            else if (!backup.ContainsKey(key)) result.Removed++;
            else if (!JsonNode.DeepEquals(current[key], backup[key])) result.Changed++;
        }
        return result;
    }

    private static SettingsBackupDiff DiffBytes(Dictionary<string, byte[]> current, IReadOnlyDictionary<string, byte[]> backup)
    {
        var result = new SettingsBackupDiff { Current = current.Count, Backup = backup.Count };
        foreach (var key in current.Keys.Union(backup.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!current.ContainsKey(key)) result.Added++;
            else if (!backup.ContainsKey(key)) result.Removed++;
            else if (!current[key].AsSpan().SequenceEqual(backup[key])) result.Changed++;
        }
        return result;
    }

    private static Dictionary<string, JsonNode?> ConfigObject(JsonNode? root, string property)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (Property(root, property) is JsonObject obj)
            foreach (var item in obj) result[item.Key] = item.Value;
        return result;
    }

    private static Dictionary<string, JsonNode?> ConfigItems(JsonNode? root, string property,
        string identity, string? fallbackIdentity = null)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (Property(root, property) is not JsonArray array) return result;
        var index = 0;
        foreach (var item in array)
        {
            var key = Text(Property(item, identity));
            if (string.IsNullOrWhiteSpace(key) && fallbackIdentity != null) key = Text(Property(item, fallbackIdentity));
            if (string.IsNullOrWhiteSpace(key)) key = "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result[key] = item;
            index++;
        }
        return result;
    }

    private static JsonNode? ParseNode(string json) => JsonNode.Parse(json, documentOptions: new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    });

    private static JsonNode? Property(JsonNode? node, string name)
    {
        if (node is not JsonObject obj) return null;
        foreach (var property in obj)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string SafeSourceName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = string.Concat(name.Select(c => char.IsControl(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(name)) name = "(unbekannt)";
        return name.Length > 255 ? name[..255] : name;
    }

    internal void CleanupExpiredPreviews() => CleanupPending();

    private void CleanupPending(bool removeAllOrphans = false, bool makeRoom = false)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in pending.Where(item => item.Value.ExpiresAtUtc < now).ToList())
            RemovePending(item.Key);
        if (makeRoom)
            foreach (var item in pending.OrderBy(item => item.Value.ExpiresAtUtc).Take(Math.Max(0, pending.Count - MaxPendingPreviews + 1)).ToList())
                RemovePending(item.Key);

        if (!Directory.Exists(previewDirectory)) return;
        var activePaths = pending.Values.Select(item => Path.GetFullPath(item.FilePath)).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(previewDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                    activePaths.Contains(Path.GetFullPath(path))) continue;
                if (!removeAllOrphans && File.GetLastWriteTimeUtc(path) > now.Subtract(PreviewLifetime)) continue;
                DeletePreview(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not enumerate preview directory {Path}", previewDirectory);
        }
    }

    private void RemovePending(string token)
    {
        if (!pending.TryRemove(token, out var item)) return;
        DeletePreview(item.FilePath);
    }

    private void DeletePreview(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { logger.LogDebug(ex, "Could not delete preview {Path}", path); }
    }

    private sealed record PendingPreview(string FilePath, string SourceFileName, string ManifestHash,
        string User, DateTimeOffset ExpiresAtUtc, bool RequireFull, IReadOnlyList<string> ScriptFilesToRemove);
}
