using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.Backup;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Backup;

internal sealed class SettingsBackupException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record SettingsBackupPayload(
    string Kind,
    byte[]? Config,
    byte[]? Scripts,
    IReadOnlyDictionary<string, byte[]> ScriptFiles);

internal sealed record SettingsBackupPackage(
    SettingsBackupManifest Manifest,
    string ManifestHash,
    byte[]? Config,
    byte[]? Scripts,
    IReadOnlyDictionary<string, byte[]> ScriptFiles);

internal static class SettingsBackupArchive
{
    internal const long MaxUploadBytes = 50L * 1024 * 1024;
    internal const int MaxEntries = 1024;
    internal const long MaxTotalUncompressedBytes = 100L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxJsonBytes = 16L * 1024 * 1024;
    private const long MaxScriptBytes = 16L * 1024 * 1024;
    private static readonly string[] AllowedScriptExtensions = [".ps1", ".psm1", ".psd1", ".txt"];
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static bool IsAllowedScript(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || char.IsWhiteSpace(fileName[0]) || char.IsWhiteSpace(fileName[^1]) ||
            fileName[0] == '.' || fileName[^1] == '.' ||
            fileName.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
            fileName.IndexOfAny(['\\', '/', '<', '>', ':', '"', '|', '?', '*']) >= 0)
            return false;

        var deviceName = fileName.Split('.')[0].TrimEnd()
            .Replace('\u00b9', '1').Replace('\u00b2', '2').Replace('\u00b3', '3');
        return !ReservedWindowsDeviceNames.Contains(deviceName) &&
               AllowedScriptExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    internal static byte[] Build(SettingsBackupPayload payload, string version, DateTimeOffset createdAtUtc,
        string hostName, string user)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (payload.Config != null) files["config.json"] = payload.Config;
        if (payload.Scripts != null) files["scripts.json"] = payload.Scripts;
        foreach (var script in payload.ScriptFiles) files["scripts/" + script.Key] = script.Value;
        if (files.Count + 1 > MaxEntries)
            throw new SettingsBackupException($"Die Sicherung enthält mehr als {MaxEntries} ZIP-Einträge.");
        long total = 0;
        foreach (var file in files)
        {
            if (file.Value.LongLength > EntryLimit(file.Key))
                throw new SettingsBackupException($"Die Datei '{file.Key}' überschreitet das Größenlimit.");
            total = checked(total + file.Value.LongLength);
            if (total > MaxTotalUncompressedBytes)
                throw new SettingsBackupException("Die entpackte Gesamtgröße überschreitet 100 MB.");
        }

        var manifest = new SettingsBackupManifest
        {
            FormatVersion = 1,
            BackupKind = payload.Kind,
            SimpleAdminVersion = version,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
            HostName = hostName,
            User = user,
            Files = files.Select(file => new SettingsBackupManifestFile
            {
                Path = file.Key,
                Size = file.Value.Length,
                Sha256 = Hash(file.Value)
            }).ToList()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.LongLength > MaxManifestBytes)
            throw new SettingsBackupException("Das Manifest überschreitet das Größenlimit.");

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files) WriteEntry(archive, file.Key, file.Value);
            WriteEntry(archive, "manifest.json", manifestBytes);
        }
        return output.ToArray();
    }

    internal static SettingsBackupPackage Parse(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
                throw new SettingsBackupException($"Das ZIP muss zwischen 1 und {MaxEntries} Einträge enthalten.");

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntryName(entry.FullName, allowManifest: true);
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new SettingsBackupException($"Doppelter ZIP-Eintrag '{entry.FullName}'.");
                var limit = EntryLimit(entry.FullName);
                if (entry.Length < 0 || entry.Length > limit)
                    throw new SettingsBackupException($"Der ZIP-Eintrag '{entry.FullName}' überschreitet das Größenlimit.");
                declaredTotal = checked(declaredTotal + entry.Length);
                if (declaredTotal > MaxTotalUncompressedBytes)
                    throw new SettingsBackupException("Die entpackte Gesamtgröße überschreitet 100 MB.");
            }

            if (!entries.ContainsKey("manifest.json"))
                throw new SettingsBackupException("Das ZIP enthält kein manifest.json.");

            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long actualTotal = 0;
            foreach (var entry in entries)
            {
                var content = ReadEntry(entry.Value, EntryLimit(entry.Key));
                actualTotal = checked(actualTotal + content.LongLength);
                if (actualTotal > MaxTotalUncompressedBytes)
                    throw new SettingsBackupException("Die entpackte Gesamtgröße überschreitet 100 MB.");
                contents.Add(entry.Key, content);
            }

            var manifestBytes = contents["manifest.json"];
            var manifest = JsonSerializer.Deserialize<SettingsBackupManifest>(manifestBytes, JsonOptions)
                ?? throw new SettingsBackupException("Das Manifest ist leer.");
            ValidateManifest(manifest, contents);

            byte[]? config = contents.GetValueOrDefault("config.json");
            byte[]? scripts = contents.GetValueOrDefault("scripts.json");
            if (manifest.BackupKind == "full" && (config == null || scripts == null))
                throw new SettingsBackupException("Eine vollständige Sicherung benötigt config.json und scripts.json.");
            if (manifest.BackupKind == "config" && (config == null || scripts != null))
                throw new SettingsBackupException("Die automatische config.json-Sicherung hat einen ungültigen Inhalt.");
            if (manifest.BackupKind == "scripts" && (scripts == null || config != null))
                throw new SettingsBackupException("Die automatische scripts.json-Sicherung hat einen ungültigen Inhalt.");
            if (config != null) ValidateConfig(config);
            if (scripts != null) ScriptDefinitionStore.ValidateJson(DecodeUtf8(scripts));

            var scriptFiles = contents.Where(pair => pair.Key.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key["scripts/".Length..], pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return new SettingsBackupPackage(manifest, Hash(manifestBytes), config, scripts, scriptFiles);
        }
        catch (SettingsBackupException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or ArgumentException or OverflowException)
        {
            throw new SettingsBackupException("Das ZIP oder sein Manifest ist ungültig: " + ex.Message, ex);
        }
    }

    internal static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new SettingsBackupException("Eine JSON-Datei ist nicht gültig UTF-kodiert.", ex);
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateManifest(SettingsBackupManifest manifest, Dictionary<string, byte[]> contents)
    {
        if (manifest.FormatVersion != 1)
            throw new SettingsBackupException($"Nicht unterstützte Format-Version {manifest.FormatVersion}; erwartet wird 1.");
        if (manifest.BackupKind is not ("full" or "config" or "scripts"))
            throw new SettingsBackupException("Unbekannter Sicherungstyp im Manifest.");
        if (string.IsNullOrWhiteSpace(manifest.SimpleAdminVersion) || string.IsNullOrWhiteSpace(manifest.HostName) ||
            string.IsNullOrWhiteSpace(manifest.User) || manifest.CreatedAtUtc == default)
            throw new SettingsBackupException("Das Manifest ist unvollständig.");
        if (manifest.Files == null || manifest.Files.Count != contents.Count - 1)
            throw new SettingsBackupException("Manifest und ZIP-Inhalt stimmen nicht überein.");

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateEntryName(file.Path, allowManifest: false);
            if (!expected.Add(file.Path) || !contents.TryGetValue(file.Path, out var content))
                throw new SettingsBackupException($"Manifest und ZIP-Inhalt stimmen bei '{file.Path}' nicht überein.");
            if (file.Size != content.LongLength || file.Sha256.Length != 64 ||
                !string.Equals(file.Sha256, Hash(content), StringComparison.OrdinalIgnoreCase))
                throw new SettingsBackupException($"SHA-256 oder Dateigröße stimmt bei '{file.Path}' nicht.");
        }
        if (expected.Count != contents.Count - 1)
            throw new SettingsBackupException("Nicht alle ZIP-Dateien sind im Manifest aufgeführt.");
    }

    private static void ValidateConfig(byte[] bytes)
    {
        try { ServiceConfigValidator.DeserializeAndValidate(DecodeUtf8(bytes)); }
        catch (InvalidOperationException ex)
        {
            throw new SettingsBackupException("Die config.json ist ungültig: " + ex.Message, ex);
        }
    }

    private static void ValidateEntryName(string name, bool allowManifest)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.IndexOfAny(['\\', '/', '\0']) >= 0 &&
            name is not ("config.json" or "scripts.json" or "manifest.json") && !name.StartsWith("scripts/", StringComparison.Ordinal))
            throw new SettingsBackupException($"Nicht erlaubter ZIP-Eintrag '{name}'.");
        if (name.StartsWith('/') || name.StartsWith('\\') || Path.IsPathRooted(name) || name.Contains('\\') || name.Contains('\0'))
            throw new SettingsBackupException($"Unsicherer ZIP-Pfad '{name}'.");
        if (name.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new SettingsBackupException($"Pfad-Traversal ist nicht zulässig: '{name}'.");
        if (name == "manifest.json" && !allowManifest)
            throw new SettingsBackupException("Das Manifest darf keinen eigenen Datei-Hash enthalten.");
        if (name is not ("config.json" or "scripts.json" or "manifest.json"))
        {
            if (!name.StartsWith("scripts/", StringComparison.Ordinal) || name.Count(c => c == '/') != 1)
                throw new SettingsBackupException($"Nicht erlaubter ZIP-Eintrag '{name}'.");
            var fileName = name["scripts/".Length..];
            if (fileName.Length == 0 || fileName.EndsWith(' ') || fileName.EndsWith('.') ||
                fileName.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') || !IsAllowedScript(fileName))
                throw new SettingsBackupException($"Nicht erlaubte Skriptdatei '{name}'.");
        }
        if (name is not ("config.json" or "scripts.json" or "manifest.json") && name != "scripts/" + name["scripts/".Length..])
            throw new SettingsBackupException($"Nicht erlaubter ZIP-Eintrag '{name}'.");
        if (name is "config.json" or "scripts.json" or "manifest.json") return;
        if (Path.GetFileName(name["scripts/".Length..]) != name["scripts/".Length..])
            throw new SettingsBackupException($"Unsicherer Skriptdateiname '{name}'.");
    }

    private static long EntryLimit(string name) => name switch
    {
        "manifest.json" => MaxManifestBytes,
        "config.json" or "scripts.json" => MaxJsonBytes,
        _ => MaxScriptBytes
    };

    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        using var input = entry.Open();
        using var output = new MemoryStream(entry.Length > 0 ? (int)entry.Length : 0);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > limit)
                throw new SettingsBackupException($"Der ZIP-Eintrag '{entry.FullName}' überschreitet das Größenlimit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
