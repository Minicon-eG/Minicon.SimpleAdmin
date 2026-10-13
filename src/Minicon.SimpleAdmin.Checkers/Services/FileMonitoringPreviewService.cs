using System.Text.RegularExpressions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>A ready-made log error signature offered by the setup assistant.</summary>
public sealed record LogSignatureTemplate(string Name, string Severity, string Regex);

/// <summary>Result of a directory dry run (what the worker would report right now).</summary>
public sealed class DirectoryPreview
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public bool TimedOut { get; set; }
    public int TotalFiles { get; set; }
    public int MatchingFiles { get; set; }
    public int WarningMinutes { get; set; }
    public int CriticalMinutes { get; set; }
    public bool CutoffActiveNow { get; set; }
    /// <summary>File extensions found (incl. the dot, "(ohne)" for none), most frequent first.</summary>
    public List<ExtensionCount> Extensions { get; set; } = new();
    /// <summary>Oldest matching files (after include/exclude), oldest first.</summary>
    public List<PreviewFile> OldestFiles { get; set; } = new();
    public int FlaggedCount { get; set; }
    public List<StuckFile> Flagged { get; set; } = new();
}

/// <summary>Result of a log scan dry run.</summary>
public sealed class LogScanPreview
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public bool TimedOut { get; set; }
    /// <summary>True when pasted sample lines were tested instead of the files.</summary>
    public bool FromSample { get; set; }
    public List<PreviewFile> Files { get; set; } = new();
    public int FilesScanned { get; set; }
    public int LinesRead { get; set; }
    /// <summary>Last lines of the newest scanned file (shortened) — to write signatures against.</summary>
    public List<string> Tail { get; set; } = new();
    public List<PatternPreview> Patterns { get; set; } = new();
    /// <summary>Built-in signature templates with their hit count in the read lines.</summary>
    public List<PatternPreview> Suggestions { get; set; } = new();
}

public sealed record ExtensionCount(string Extension, int Count);

public sealed record PreviewFile(string Path, DateTime LastModified, double AgeMinutes, bool Included);

public sealed class PatternPreview
{
    public string Name { get; set; } = string.Empty;
    public string Severity { get; set; } = "Critical";
    public string Regex { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int Count { get; set; }
    public List<string> Samples { get; set; } = new();
}

/// <summary>
/// Dry run for the file monitoring setup assistant in the WebUI. Uses the same evaluation as
/// <see cref="FileShareChecker"/> (age thresholds, cutoff, include/exclude) and the same
/// <see cref="IFileSystemAccess"/>, but returns a richer, size-limited result: extensions found,
/// oldest files, log tail, per-signature hit counts with sample lines and invalid-regex errors.
/// Regexes run with a match timeout so a pathological pattern cannot block the request.
/// </summary>
public sealed class FileMonitoringPreviewService(IFileSystemAccess fileSystem)
{
    internal const int MaxListedFiles = 20;
    internal const int MaxTailLines = 30;
    internal const int MaxLineLength = 300;
    internal const int MaxSamplesPerPattern = 3;
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Common error signatures; the assistant shows them with their hit count.</summary>
    public static IReadOnlyList<LogSignatureTemplate> SignatureTemplates { get; } = new[]
    {
        new LogSignatureTemplate("Exception", "Critical", @"exception\b"),
        new LogSignatureTemplate("Fehler (ERROR)", "Critical", @"\b(error|fehler)\b"),
        new LogSignatureTemplate("Schwerer Fehler (FATAL)", "Critical", @"\b(fatal|critical|kritisch)\b"),
        new LogSignatureTemplate("Warnung (WARN)", "Warning", @"\b(warn|warning|warnung)\b"),
        new LogSignatureTemplate("Zeitüberschreitung", "Warning", @"time[ d-]?out|zeitüberschreitung"),
        new LogSignatureTemplate("Verbindungsfehler", "Warning", @"connection (refused|reset|closed|failed)|verbindung.*(fehlgeschlagen|abgelehnt|getrennt)"),
        new LogSignatureTemplate("Zugriff verweigert", "Critical", @"access (is )?denied|zugriff verweigert|unauthori[sz]ed"),
        new LogSignatureTemplate("Speichermangel", "Critical", @"out ?of ?memory"),
        new LogSignatureTemplate("Deadlock", "Critical", @"deadlock"),
    };

    public async Task<DirectoryPreview> PreviewDirectoryAsync(
        WatchedDirectory dir, FileMonitoringDefaults defaults, CancellationToken cancellationToken = default)
    {
        var result = new DirectoryPreview();
        if (string.IsNullOrWhiteSpace(dir.Path))
        {
            result.Error = "Bitte einen Pfad angeben.";
            return result;
        }

        IReadOnlyList<FileEntry> files;
        try
        {
            files = await RunWithTimeout(() => fileSystem.EnumerateFiles(dir.Path.Trim(), dir.Recursive), cancellationToken);
        }
        catch (TimeoutException)
        {
            result.TimedOut = true;
            result.Error = $"Keine Antwort nach {IoTimeout.TotalSeconds:0} s – Pfad zu groß oder nicht erreichbar.";
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Error = ex.Message;
            return result;
        }

        return BuildDirectoryPreview(files, dir, defaults, DateTime.Now);
    }

    internal static DirectoryPreview BuildDirectoryPreview(
        IReadOnlyList<FileEntry> files, WatchedDirectory dir, FileMonitoringDefaults defaults, DateTime now)
    {
        var matching = files.Where(f => IsIncluded(f.Name, dir)).ToList();
        var flagged = FileShareChecker.EvaluateDirectory(files, dir, defaults, now)
            .OrderByDescending(s => s.IsCritical).ThenByDescending(s => s.AgeMinutes)
            .ToList();
        var cutoff = TimeSpan.TryParse(dir.CutoffTime, out var ts) ? ts : (TimeSpan?)null;

        return new DirectoryPreview
        {
            Reachable = true,
            TotalFiles = files.Count,
            MatchingFiles = matching.Count,
            WarningMinutes = dir.AgeMinutesWarning ?? defaults.StuckFileAgeMinutesWarning,
            CriticalMinutes = dir.AgeMinutesCritical ?? defaults.StuckFileAgeMinutesCritical,
            CutoffActiveNow = cutoff.HasValue && now.TimeOfDay >= cutoff.Value,
            Extensions = files
                .GroupBy(f => string.IsNullOrEmpty(Path.GetExtension(f.Name)) ? "(ohne)" : Path.GetExtension(f.Name).ToLowerInvariant())
                .Select(g => new ExtensionCount(g.Key, g.Count()))
                .OrderByDescending(e => e.Count).ThenBy(e => e.Extension)
                .Take(10)
                .ToList(),
            OldestFiles = matching
                .OrderBy(f => f.LastWriteTime)
                .Take(MaxListedFiles)
                .Select(f => new PreviewFile(f.FullPath, f.LastWriteTime, Math.Round((now - f.LastWriteTime).TotalMinutes, 1), true))
                .ToList(),
            FlaggedCount = flagged.Count,
            Flagged = flagged.Take(MaxListedFiles).ToList()
        };
    }

    /// <summary>
    /// Tests the signatures of <paramref name="scan"/> against the real log files — or, when
    /// <paramref name="sampleText"/> is set, against the pasted lines without touching the file system.
    /// </summary>
    public async Task<LogScanPreview> PreviewLogScanAsync(
        LogScanConfig scan, string? sampleText, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sampleText))
        {
            var sampleLines = sampleText.Replace("\r\n", "\n").Split('\n').ToList();
            var preview = new LogScanPreview { Reachable = true, FromSample = true };
            FillMatches(preview, sampleLines, scan.Patterns);
            return preview;
        }

        var result = new LogScanPreview();
        if (string.IsNullOrWhiteSpace(scan.Path))
        {
            result.Error = "Bitte einen Pfad oder ein Muster (z. B. C:\\Logs\\*.log) angeben.";
            return result;
        }

        try
        {
            var now = DateTime.Now;
            var (files, lines, tail) = await RunWithTimeout(() => ReadLogs(scan, now), cancellationToken);
            result.Reachable = true;
            result.Files = files;
            result.FilesScanned = files.Count(f => f.Included);
            result.LinesRead = lines.Count;
            result.Tail = tail;
            if (files.Count == 0)
            {
                result.Error = "Keine passende Datei gefunden.";
            }

            FillMatches(result, lines, scan.Patterns);
        }
        catch (TimeoutException)
        {
            result.TimedOut = true;
            result.Error = $"Keine Antwort nach {IoTimeout.TotalSeconds:0} s – Pfad nicht erreichbar oder Dateien zu groß.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private (List<PreviewFile> Files, List<string> Lines, List<string> Tail) ReadLogs(LogScanConfig scan, DateTime now)
    {
        var resolved = fileSystem.ResolveLogFiles(scan.Path.Trim())
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();
        var files = new List<PreviewFile>();
        var lines = new List<string>();
        var tail = new List<string>();

        foreach (var file in resolved)
        {
            var age = (now - file.LastWriteTime).TotalMinutes;
            var included = scan.MaxFileAgeMinutes is not (int maxAge and > 0) || age <= maxAge;
            if (files.Count < MaxListedFiles)
            {
                files.Add(new PreviewFile(file.FullPath, file.LastWriteTime, Math.Round(age, 1), included));
            }

            if (!included)
            {
                continue;
            }

            var fileLines = fileSystem.ReadLines(file.FullPath, scan.MaxLines);
            if (tail.Count == 0)
            {
                tail = fileLines.Skip(Math.Max(0, fileLines.Count - MaxTailLines)).Select(Shorten).ToList();
            }

            lines.AddRange(fileLines);
        }

        return (files, lines, tail);
    }

    internal static void FillMatches(LogScanPreview preview, IReadOnlyList<string> lines, IEnumerable<LogPatternConfig> patterns)
    {
        preview.LinesRead = lines.Count;
        preview.Patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p.Regex))
            .Select(p => Match(p.Name, p.Severity, p.Regex, lines))
            .ToList();
        preview.Suggestions = SignatureTemplates
            .Select(t => Match(t.Name, t.Severity, t.Regex, lines))
            .ToList();
    }

    internal static PatternPreview Match(string name, string severity, string pattern, IReadOnlyList<string> lines)
    {
        var result = new PatternPreview
        {
            Name = string.IsNullOrWhiteSpace(name) ? pattern : name,
            Severity = string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Critical",
            Regex = pattern
        };

        Regex regex;
        try
        {
            // Same options as FileShareChecker.EvaluateLogContent, plus a timeout for the interactive test.
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            result.Error = "Ungültiger regulärer Ausdruck: " + ex.Message;
            return result;
        }

        try
        {
            foreach (var line in lines)
            {
                if (!regex.IsMatch(line))
                {
                    continue;
                }

                result.Count++;
                if (result.Samples.Count < MaxSamplesPerPattern)
                {
                    result.Samples.Add(Shorten(line.Trim()));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            result.Error = "Ausdruck zu aufwendig (Zeitüberschreitung bei der Auswertung) – bitte vereinfachen.";
        }

        return result;
    }

    private static bool IsIncluded(string name, WatchedDirectory dir)
        => (dir.IncludePatterns.Count == 0 || dir.IncludePatterns.Any(p => MatchesGlob(p, name)))
           && !dir.ExcludePatterns.Any(p => MatchesGlob(p, name));

    private static bool MatchesGlob(string pattern, string name)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private static string Shorten(string line)
        => line.Length <= MaxLineLength ? line : line[..MaxLineLength] + " …";

    private static async Task<T> RunWithTimeout<T>(Func<T> work, CancellationToken cancellationToken)
        => await Task.Run(work, cancellationToken).WaitAsync(IoTimeout, cancellationToken);
}
