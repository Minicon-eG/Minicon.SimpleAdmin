namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// A single file discovered on a watched share / log path.
/// <paramref name="Size"/> is the file length in bytes as seen during the enumeration
/// (0 when unknown). It is the basis of the size-change detection in <see cref="FileShareChecker"/>.
/// </summary>
public sealed record FileEntry(string FullPath, string Name, DateTime LastWriteTime, long Size = 0);

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

    /// <summary>
    /// Reads the lines of a file that were appended since <paramref name="offset"/> (byte position) and
    /// returns the new position. A file that was truncated or rotated (shorter than the offset) is read
    /// from the beginning again.
    /// </summary>
    LogFileReadResult ReadFrom(string path, long offset, int maxLines);
}

/// <summary>
/// Result of reading a log file from a remembered byte position: the new lines and the position
/// behind them (plus the file length / write time at the time of the read).
/// </summary>
public sealed record LogFileReadResult(IReadOnlyList<string> Lines, long Offset, long Length, DateTime LastWriteTime);

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
            result.Add(new FileEntry(path, info.Name, info.LastWriteTime, SafeLength(info)));
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
                    return new FileEntry(p, info.Name, info.LastWriteTime, SafeLength(info));
                })
                .ToList();
        }

        var single = new FileInfo(pathOrGlob);
        return single.Exists
            ? new[] { new FileEntry(pathOrGlob, single.Name, single.LastWriteTime, SafeLength(single)) }
            : Array.Empty<FileEntry>();
    }

    /// <summary>File length in bytes; 0 when the file vanished between enumeration and stat.</summary>
    private static long SafeLength(FileInfo info)
    {
        try
        {
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            // Share went offline mid-scan - size unknown, no size-change finding this cycle.
            return 0;
        }
    }

    public IReadOnlyList<string> ReadLines(string path, int maxLines)
    {
        var all = File.ReadAllLines(path);
        if (maxLines <= 0 || all.Length <= maxLines)
        {
            return all;
        }

        return all[^maxLines..];
    }

    public LogFileReadResult ReadFrom(string path, long offset, int maxLines)
    {
        var info = new FileInfo(path);
        var length = info.Exists ? info.Length : 0;
        var lastWrite = info.Exists ? info.LastWriteTime : DateTime.MinValue;

        // Truncated or rotated file: the remembered position no longer exists — read from the start.
        var start = offset > 0 && offset <= length ? offset : 0;

        if (!info.Exists)
        {
            return new LogFileReadResult(Array.Empty<string>(), 0, length, lastWrite);
        }

        var lines = new List<string>();
        long position = start;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                // The reader hides the byte position, so the length of the consumed text is tracked
                // separately: StreamReader with detectEncodingFromByteOrderMarks may swallow a BOM.
                lines.Add(line);
                if (maxLines > 0 && lines.Count >= maxLines)
                {
                    break;
                }
            }

            position = stream.Position;
        }

        // A truncated read (MaxLines reached) must not skip the rest of the file: the position is only
        // advanced to the end of the lines that were actually read.
        return new LogFileReadResult(lines, position, length, lastWrite);
    }
}
