using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Remembered read position of one watched log file. The position survives a worker restart, so a
/// log line is reported exactly once even when the file is scanned in many cycles.
/// </summary>
public sealed class LogFileOffset
{
    /// <summary>Byte position of the first line that has not been reported yet.</summary>
    public long Offset { get; set; }

    /// <summary>File length at the time of the last read (used to detect rotation / truncation).</summary>
    public long Length { get; set; }

    /// <summary>Last-write timestamp of the file at the time of the last read.</summary>
    public DateTime LastWriteTime { get; set; }
}

/// <summary>
/// Persistent store for the read positions of the watched log files, keyed by the file path.
/// </summary>
public interface ILogFileOffsetStore
{
    /// <summary>Returns the remembered position of a file, or <c>null</c> if the file is unknown.</summary>
    LogFileOffset? Get(string key);

    /// <summary>Remembers the position of a file.</summary>
    void Set(string key, LogFileOffset offset);

    /// <summary>Persists the remembered positions (no-op for stores that do not persist).</summary>
    void Save();
}

/// <summary>
/// File-backed <see cref="ILogFileOffsetStore"/>: one JSON file with the read position per log file.
/// The file is written atomically (temp file + move), so a crash cannot leave a half-written store.
/// </summary>
public sealed class JsonLogFileOffsetStore : ILogFileOffsetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly ILogger<JsonLogFileOffsetStore> _logger;
    private readonly Dictionary<string, LogFileOffset> _offsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads an existing store (if present) and keeps it in memory until <see cref="Save"/>.</summary>
    public JsonLogFileOffsetStore(string filePath, ILogger<JsonLogFileOffsetStore> logger)
    {
        _filePath = filePath;
        _logger = logger;

        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LogFileOffset>>(json, JsonOptions);
            if (loaded is not null)
            {
                foreach (var (key, value) in loaded)
                {
                    if (!string.IsNullOrWhiteSpace(key) && value is not null)
                    {
                        _offsets[key] = value;
                    }
                }

                _logger.LogInformation("Log-Offset-Speicher geladen: {Path} ({Count} Einträge)", filePath, _offsets.Count);
            }
        }
        catch (Exception ex)
        {
            // A broken store must not stop the monitoring: start without offsets (the next cycle reads
            // the tail of the logs again).
            _logger.LogWarning(ex, "Log-Offset-Speicher '{Path}' nicht lesbar, beginne ohne Versatz", filePath);
        }
    }

    public LogFileOffset? Get(string key)
        => !string.IsNullOrWhiteSpace(key) && _offsets.TryGetValue(key, out var offset) ? offset : null;

    public void Set(string key, LogFileOffset offset)
    {
        if (string.IsNullOrWhiteSpace(key) || offset is null)
        {
            return;
        }

        _offsets[key] = offset;
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_offsets, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);

            _logger.LogDebug("Log-Offset-Speicher gespeichert: {Path} ({Count} Einträge)", _filePath, _offsets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log-Offset-Speicher '{Path}' konnte nicht geschrieben werden", _filePath);
        }
    }
}

/// <summary>
/// Store that remembers nothing — used when the offset tracking is switched off. The checker then
/// reads the tail of the log in every cycle (old behaviour).
/// </summary>
public sealed class NoOpLogFileOffsetStore : ILogFileOffsetStore
{
    public LogFileOffset? Get(string key) => null;

    public void Set(string key, LogFileOffset offset)
    {
        // Nothing is remembered.
    }

    public void Save()
    {
        // Nothing to persist.
    }
}

/// <summary>
/// Store that keeps the positions only in memory — used where no persistence is wanted (tests,
/// dry runs). The positions are lost when the process ends.
/// </summary>
public sealed class InMemoryLogFileOffsetStore : ILogFileOffsetStore
{
    private readonly Dictionary<string, LogFileOffset> _offsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of remembered positions (for tests).</summary>
    public int Count => _offsets.Count;

    public LogFileOffset? Get(string key)
        => !string.IsNullOrWhiteSpace(key) && _offsets.TryGetValue(key, out var offset) ? offset : null;

    public void Set(string key, LogFileOffset offset)
    {
        if (!string.IsNullOrWhiteSpace(key) && offset is not null)
        {
            _offsets[key] = offset;
        }
    }

    public void Save()
    {
        // Nothing to persist.
    }
}
