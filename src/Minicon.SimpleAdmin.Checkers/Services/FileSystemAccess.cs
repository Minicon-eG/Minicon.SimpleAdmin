using System.Text;

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

    /// <summary>
    /// Reads the lines that were appended to a file since <paramref name="offset"/> (a byte position)
    /// and returns the position behind them. A file that was truncated or rotated (shorter than the
    /// offset) is read from the beginning again.
    /// </summary>
    LogFileReadResult ReadFrom(string path, long offset, int maxLines);

    /// <summary>
    /// Reads the last <paramref name="maxLines"/> lines of a file (0 = whole file) and returns the
    /// position behind them — used for the first cycle, where no position is known yet.
    /// </summary>
    LogFileReadResult ReadTail(string path, int maxLines);
}

/// <summary>
/// Result of reading a log file from a remembered byte position: the new lines, the position behind
/// them and the file length / write time at the time of the read.
/// </summary>
public sealed record LogFileReadResult(
    IReadOnlyList<string> Lines,
    long Offset,
    long Length,
    DateTime LastWriteTime);

/// <summary>Default <see cref="IFileSystemAccess"/> backed by <see cref="System.IO"/>.</summary>
public sealed class FileSystemAccess : IFileSystemAccess
{
    /// <summary>
    /// Upper limit of bytes read in one call. The read stops at the end of the last complete line
    /// inside this window, so nothing is skipped.
    /// </summary>
    private const int MaxReadBytes = 16 * 1024 * 1024;

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
        if (!File.Exists(path))
        {
            return new LogFileReadResult(Array.Empty<string>(), 0, 0, DateTime.MinValue);
        }

        var info = new FileInfo(path);
        var length = info.Length;
        var lastWrite = info.LastWriteTime;

        // Truncated or rotated file: the remembered position is gone, read from the beginning.
        var start = offset > 0 && offset <= length ? offset : 0;

        var data = ReadWindow(path, start);
        var lines = new List<string>();
        var consumed = 0L;
        var index = 0;

        while (index < data.Length)
        {
            var newline = Array.IndexOf(data, (byte)'\n', index);
            if (newline < 0)
            {
                // The last line has no line break yet — the writer may still be appending to it.
                // It is not consumed, so the next read sees the complete line.
                break;
            }

            if (maxLines > 0 && lines.Count >= maxLines)
            {
                // More new lines than allowed: the rest is read in the next cycle, nothing is skipped.
                break;
            }

            lines.Add(DecodeLine(data, index, newline - index));
            consumed += newline + 1 - index;
            index = newline + 1;
        }

        return new LogFileReadResult(lines, start + consumed, length, lastWrite);
    }

    public LogFileReadResult ReadTail(string path, int maxLines)
    {
        if (!File.Exists(path))
        {
            return new LogFileReadResult(Array.Empty<string>(), 0, 0, DateTime.MinValue);
        }

        var info = new FileInfo(path);
        var length = info.Length;
        var lastWrite = info.LastWriteTime;

        var data = ReadWindow(path, 0);
        var lines = SplitLines(data);

        if (maxLines > 0 && lines.Count > maxLines)
        {
            lines = lines[^maxLines..];
        }

        // The position behind the tail: the end of the file, but only if the last line is complete.
        // An incomplete last line stays unread, so it is reported with its content in the next cycle.
        var offset = length;
        if (!EndsWithNewline(data))
        {
            var lastBreak = data.Length == 0 ? -1 : Array.LastIndexOf(data, (byte)'\n');
            offset = lastBreak < 0 ? 0 : lastBreak + 1;
        }

        return new LogFileReadResult(lines, offset, length, lastWrite);
    }

    /// <summary>Reads up to <see cref="MaxReadBytes"/> bytes of the file, starting at <paramref name="start"/>.</summary>
    private static byte[] ReadWindow(string path, long start)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (start > 0)
        {
            stream.Seek(start, SeekOrigin.Begin);
        }

        var length = (int)Math.Min(MaxReadBytes, Math.Max(0, stream.Length - stream.Position));
        var buffer = new byte[length];
        var read = 0;

        while (read < length)
        {
            var chunk = stream.Read(buffer, read, length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return read == length ? buffer : buffer[..read];
    }

    private static List<string> SplitLines(byte[] data)
    {
        var lines = new List<string>();
        var index = 0;

        while (index < data.Length)
        {
            var newline = Array.IndexOf(data, (byte)'\n', index);
            if (newline < 0)
            {
                lines.Add(DecodeLine(data, index, data.Length - index));
                break;
            }

            lines.Add(DecodeLine(data, index, newline - index));
            index = newline + 1;
        }

        return lines;
    }

    private static bool EndsWithNewline(byte[] data)
        => data.Length > 0 && data[^1] == (byte)'\n';

    private static string DecodeLine(byte[] data, int start, int length)
        => Encoding.UTF8.GetString(data, start, length).TrimEnd('\r');
}
