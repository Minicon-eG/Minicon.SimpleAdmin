namespace Minicon.SimpleAdmin.WebUI.Models.Backup;

public sealed class SettingsBackupManifest
{
    public int FormatVersion { get; set; }
    public string BackupKind { get; set; } = "full";
    public string SimpleAdminVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public List<SettingsBackupManifestFile> Files { get; set; } = new();
}

public sealed class SettingsBackupManifestFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SettingsBackupDiff
{
    public int Current { get; set; }
    public int Backup { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Changed { get; set; }
}

public sealed class SettingsBackupPreview
{
    public string Token { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public SettingsBackupManifest Manifest { get; set; } = new();
    public string InstalledVersion { get; set; } = string.Empty;
    public bool NewerVersion { get; set; }
    public int EncryptedValues { get; set; }
    public int UndecryptableValues { get; set; }
    public SettingsBackupDiff Servers { get; set; } = new();
    public SettingsBackupDiff Environments { get; set; } = new();
    public SettingsBackupDiff Domains { get; set; } = new();
    public SettingsBackupDiff Scripts { get; set; } = new();
    public SettingsBackupDiff ScriptFiles { get; set; } = new();
    public IReadOnlyList<string> ScriptFilesToRemove { get; set; } = Array.Empty<string>();
}

public sealed class AutomaticSettingsBackup
{
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public long Size { get; set; }
    public string Kind { get; set; } = string.Empty;
}

public sealed class SettingsBackupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? PreviousBackupFileName { get; set; }
}

public sealed class SettingsBackupPageViewModel
{
    public bool RestoreEnabled { get; set; }
    public string? RestoreDisabledReason { get; set; }
    public string InstalledVersion { get; set; } = string.Empty;
    public long MaxUploadBytes { get; set; }
    public SettingsBackupPreview? Preview { get; set; }
    public SettingsBackupResult? Result { get; set; }
    public List<AutomaticSettingsBackup> AutomaticBackups { get; set; } = new();
}
