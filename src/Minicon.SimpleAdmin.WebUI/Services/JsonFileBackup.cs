using System.Globalization;

namespace Minicon.SimpleAdmin.WebUI.Services;

internal static class JsonFileBackup
{
    private const int RetainedBackups = 20;

    internal static string UtcTimestamp() =>
        DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static (string BaseName, int Suffix) BackupNameAndSuffix(string path)
    {
        var name = Path.GetFileName(path);
        var separator = name.LastIndexOf('-');
        if (separator >= 0 && int.TryParse(name[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var suffix))
            return (name[..separator], suffix);
        return (name, 0);
    }

    internal static void TryCreate(string filePath, ILogger logger)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            var backupDirectory = Path.Combine(directory, "backup");
            Directory.CreateDirectory(backupDirectory);

            var fileName = Path.GetFileName(fullPath);
            var timestamp = UtcTimestamp();
            var destination = Path.Combine(backupDirectory, fileName + "." + timestamp);
            for (var suffix = 1; File.Exists(destination); suffix++)
                destination = Path.Combine(backupDirectory, $"{fileName}.{timestamp}-{suffix:000}");

            File.Copy(fullPath, destination);

            var expired = Directory.EnumerateFiles(backupDirectory, fileName + ".*")
                .OrderByDescending(File.GetCreationTimeUtc)
                .ThenByDescending(path => BackupNameAndSuffix(path).BaseName, StringComparer.Ordinal)
                .ThenByDescending(path => BackupNameAndSuffix(path).Suffix)
                .Skip(RetainedBackups);
            foreach (var path in expired) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backup of {Path} failed; saving continues", filePath);
        }
    }
}
