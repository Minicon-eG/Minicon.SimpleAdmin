namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Host-side settings of the PowerShell script runner (<c>appsettings.json</c> section
/// <c>ScriptRunner</c>). Deliberately NOT part of config.json: whoever can edit the
/// configuration in the UI must not be able to change these limits (allowed directories, role)
/// or re-enable execution on a host that opted out.
/// </summary>
public sealed class ScriptRunnerOptions
{
    public const string SectionName = "ScriptRunner";

    /// <summary>Master switch (default on; a host can opt out with <c>false</c>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>PowerShell executable (default: <c>powershell.exe</c> on Windows, <c>pwsh</c> elsewhere).</summary>
    public string? PowerShellPath { get; set; }

    /// <summary>Optional AD group required to define and start scripts (in addition to the WebUI's own role).</summary>
    public string? RequiredRole { get; set; }

    /// <summary>
    /// Directories scripts must live in. Empty = any path on the host. The upload directory is always allowed.
    /// </summary>
    public List<string> AllowedDirectories { get; set; } = new();

    /// <summary>
    /// Roots the server-side file/folder chooser may browse. Empty = all drives.
    /// </summary>
    public List<string> BrowseRoots { get; set; } = new();

    /// <summary>Where uploaded .ps1 files are stored (default: <c>&lt;ConfigPath&gt;/scripts</c>).</summary>
    public string? UploadDirectory { get; set; }

    /// <summary>Where run history and output logs are stored (default: <c>&lt;ConfigPath&gt;/script-runs</c>).</summary>
    public string? RunLogDirectory { get; set; }

    /// <summary>Maximum number of scripts running at the same time (default 4).</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>Output lines kept in memory per run; the log file always holds everything (default 20 000).</summary>
    public int MaxOutputLines { get; set; } = 20_000;

    /// <summary>Number of runs kept in the history (default 200).</summary>
    public int HistorySize { get; set; } = 200;

    internal string EffectivePowerShellPath =>
        !string.IsNullOrWhiteSpace(PowerShellPath) ? PowerShellPath! : OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
}
