namespace Minicon.SimpleAdmin.WebUI.ViewModels;

/// <summary>
/// Explains where the status page looks for data and why it found none. Shown on the status page
/// when no server status is available.
/// </summary>
public class StatusSourceDiagnostics
{
    public bool ConfigLoaded { get; init; }
    public string? ConfigFilePath { get; init; }
    public string? ConfigLoadError { get; init; }

    /// <summary><c>StatusDirectory</c> from appsettings.json (fallback when no centralOutputPath is set).</summary>
    public string DefaultStatusDirectory { get; init; } = string.Empty;

    /// <summary><c>output.centralOutputPath</c> from config.json, if set.</summary>
    public string? CentralOutputPath { get; init; }

    public List<DirectoryInfoEntry> Directories { get; init; } = new();

    public bool PullStatusOverHttp { get; init; }
    public bool HttpClientAvailable { get; init; }
    public int ServerCount { get; init; }
    public int ActiveServerCount { get; init; }
    public int ActiveServersWithBaseUrl { get; init; }

    /// <summary>Human-readable (German) reasons, most important first.</summary>
    public List<string> Reasons { get; init; } = new();

    public class DirectoryInfoEntry
    {
        public string Path { get; init; } = string.Empty;
        public bool Exists { get; init; }
        /// <summary>*.json files considered as status sources (acknowledges.json excluded).</summary>
        public int JsonFileCount { get; init; }
        public DateTime? NewestFileUtc { get; init; }
    }
}
