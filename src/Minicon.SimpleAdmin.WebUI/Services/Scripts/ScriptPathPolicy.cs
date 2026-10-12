namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Path rules of the script runner: which .ps1 files may be registered/executed and which
/// directories the server-side file chooser may list.
/// </summary>
public sealed class ScriptPathPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<string> _allowedDirectories;
    private readonly IReadOnlyList<string> _browseRoots;

    public ScriptPathPolicy(string uploadDirectory, IEnumerable<string> allowedDirectories, IEnumerable<string> browseRoots)
    {
        UploadDirectory = Path.GetFullPath(uploadDirectory);
        var allowed = allowedDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Normalize).ToList();
        if (allowed.Count > 0) allowed.Add(UploadDirectory);
        _allowedDirectories = allowed;
        _browseRoots = browseRoots.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Normalize).ToList();
    }

    public string UploadDirectory { get; }

    public IReadOnlyList<string> AllowedDirectories => _allowedDirectories;

    /// <summary>
    /// Checks a script path. Returns null when allowed, otherwise a German reason.
    /// </summary>
    public string? ValidateScriptPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Kein Skriptpfad angegeben.";
        if (!Path.IsPathFullyQualified(path)) return "Der Skriptpfad muss absolut sein (z. B. D:\\Skripte\\Kopie.ps1).";
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return "Nur PowerShell-Skripte (.ps1) sind erlaubt.";
        if (_allowedDirectories.Count > 0 && !_allowedDirectories.Any(d => IsUnder(full, d)))
            return $"Das Skript liegt außerhalb der freigegebenen Verzeichnisse ({string.Join(", ", _allowedDirectories)}).";
        return null;
    }

    /// <summary>Whether the file chooser may list <paramref name="directory"/>.</summary>
    public bool CanBrowse(string directory)
    {
        if (_browseRoots.Count == 0) return true;
        var full = Normalize(directory);
        return _browseRoots.Any(r => IsUnder(full, r));
    }

    /// <summary>Starting points of the file chooser (configured roots or all ready drives).</summary>
    public IReadOnlyList<string> GetRoots()
    {
        if (_browseRoots.Count > 0) return _browseRoots;
        try
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch
        {
            return new[] { Path.GetPathRoot(Environment.CurrentDirectory) ?? "/" };
        }
    }

    internal static bool IsUnder(string fullPath, string directory)
    {
        var dir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Equals(dir, PathComparison)) return true;
        // Roots ("C:\", "/") are trimmed to "C:" / "" and still match via the separator.
        return fullPath.StartsWith(dir + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
