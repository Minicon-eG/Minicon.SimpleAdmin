using System.Reflection;

namespace Minicon.SimpleAdmin.WebUI.Services;

/// <summary>
/// Release notes for the "Neuigkeiten" page. CHANGELOG.md is embedded into this assembly at
/// build time (see the csproj), so the notes always match the installed version and need no
/// file next to the deployment or internet access. Parsed once and cached.
/// </summary>
internal sealed class ChangelogProvider
{
    /// <summary>Logical name of the embedded resource (set in Minicon.SimpleAdmin.WebUI.csproj).</summary>
    internal const string ResourceName = "Minicon.SimpleAdmin.WebUI.CHANGELOG.md";

    private readonly Lazy<IReadOnlyList<ChangelogRelease>> _releases;

    public ChangelogProvider()
        : this(() => ReadEmbedded(typeof(ChangelogProvider).Assembly))
    {
    }

    internal ChangelogProvider(Func<string?> loadMarkdown)
    {
        _releases = new Lazy<IReadOnlyList<ChangelogRelease>>(() =>
        {
            var markdown = loadMarkdown();
            return string.IsNullOrWhiteSpace(markdown) ? [] : ChangelogParser.Parse(markdown);
        });
    }

    /// <summary>All releases, newest first (order of the file). Empty when the resource is missing.</summary>
    public IReadOnlyList<ChangelogRelease> Releases => _releases.Value;

    /// <summary>Installed package version (major.minor.patch), as shown in the footer.</summary>
    public static string InstalledVersion { get; } =
        typeof(ChangelogProvider).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string? ReadEmbedded(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
