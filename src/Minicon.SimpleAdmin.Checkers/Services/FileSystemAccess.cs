namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// A single file discovered on a watched share / log path.
/// </summary>
public sealed record FileEntry(string FullPath, string Name, DateTime LastWriteTime);

/// <summary>
/// Thin IO seam for the <see cref="FileShareChecker"/>. Keeps the file-system access (which is the
/// only non-deterministic / non-portable part) behind an interface so the evaluation stays pure and
/// unit-testable, mirroring the <c>IBizTalkApiClient</c> / <c>ISqlQueryExecutor</c> pattern.
/// Implementations must throw on an unreachable directory so the checker can flag the share offline.
/// </summary>
public interface IFileSystemAccess
{
    /// <summary>Enumerates the files in a directory. Throws if the directory cannot be reached.</summary>
    IReadOnlyList<FileEntry> EnumerateFiles(string directory, bool recursive);

    /// <summary>Resolves a single file path or a glob (e.g. <c>dir\*.log</c>) to matching files.</summary>
    IReadOnlyList<FileEntry> ResolveLogFiles(string pathOrGlob);

    /// <summary>Reads up to <paramref name="maxLines"/> lines from the tail of a file (0 = whole file).</summary>
    IReadOnlyList<string> ReadLines(string path, int maxLines);
}

/// <summary>Default <see cref="IFileSystemAccess"/> backed by <see cref="System.IO"/>.</summary>
public sealed class FileSystemAccess : IFileSystemAccess
{
    public IReadOnlyList<FileEntry> EnumerateFiles(string directory, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", option))
        {
            var info = new FileInfo(path);
            result.Add(new FileEntry(path, info.Name, info.LastWriteTime));
        }
        return result;
    }

    public IReadOnlyList<FileEntry> ResolveLogFiles(string pathOrGlob)
    {
        var name = Path.GetFileName(pathOrGlob);
        if (name.Contains('*') || name.Contains('?'))
        {
            var dir = Path.GetDirectoryName(pathOrGlob);
            if (string.IsNullOrEmpty(dir))
            {
                return Array.Empty<FileEntry>();
            }

            return Directory.EnumerateFiles(dir, name, SearchOption.TopDirectoryOnly)
                .Select(p =>
                {
                    var info = new FileInfo(p);
                    return new FileEntry(p, info.Name, info.LastWriteTime);
                })
                .ToList();
        }

        var single = new FileInfo(pathOrGlob);
        return single.Exists
            ? new[] { new FileEntry(pathOrGlob, single.Name, single.LastWriteTime) }
            : Array.Empty<FileEntry>();
    }

    /// <summary>
    /// Reads up to <paramref name="maxLines"/> lines from the tail of a file (0 = whole file).
    /// The file is read forward into a bounded ring buffer so that a huge log is not loaded
    /// completely into memory.
    /// </summary>
    public IReadOnlyList<string> ReadLines(string path, int maxLines)
    {
        if (maxLines <= 0)
        {
            return File.ReadAllLines(path);
        }

        return ReadTail(path, maxLines);
    }

    /// <summary>
    /// Reads the last <paramref name="maxLines"/> lines of a file. The file is read forward with a
    /// <see cref="StreamReader"/> (BOM detection, "\r", "\n" and "\r\n" as line endings) and only
    /// the requested tail is kept in memory — a bounded ring buffer instead of the whole file.
    /// <para>
    /// The result is identical to <c>File.ReadAllLines(path).TakeLast(maxLines)</c> (the reference
    /// of the previous implementation): empty lines at the beginning and at the end are kept, a
    /// UTF-8/UTF-16 BOM is not part of the first line and a file without a trailing newline does
    /// not gain one.
    /// </para>
    /// </summary>
    private static string[] ReadTail(string path, int maxLines)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, options: FileOptions.SequentialScan);

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        // A ring buffer of the last maxLines lines — the memory does not depend on the file size.
        var buffer = new string[maxLines];
        var count = 0;
        var next = 0;

        while (reader.ReadLine() is { } line)
        {
            buffer[next] = line;
            next = (next + 1) % maxLines;
            if (count < maxLines)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new string[count];
        var start = count < maxLines ? 0 : next;
        for (var i = 0; i < count; i++)
        {
            result[i] = buffer[(start + i) % maxLines];
        }

        return result;
    }
}
