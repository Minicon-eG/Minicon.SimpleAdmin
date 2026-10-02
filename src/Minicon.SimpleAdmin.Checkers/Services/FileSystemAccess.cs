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

    public IReadOnlyList<string> ReadLines(string path, int maxLines)
    {
        var all = File.ReadAllLines(path);
        if (maxLines <= 0 || all.Length <= maxLines)
        {
            return all;
        }

        return all[^maxLines..];
    }
}
