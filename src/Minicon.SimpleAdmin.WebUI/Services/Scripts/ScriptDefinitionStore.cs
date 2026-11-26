using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Persists the script definitions in <c>scripts.json</c> next to config.json (atomic write:
/// temp file + move). The file is re-read when it changes on disk, so manual edits are picked
/// up instead of being overwritten by the next save. An unreadable file is reported via
/// <see cref="LoadError"/> and never overwritten by ordinary saves.
/// </summary>
public sealed class ScriptDefinitionStore
{
    private readonly string _filePath;
    private readonly ILogger<ScriptDefinitionStore> _logger;
    private readonly object _lock = new();
    private List<ScriptDefinition> _cache = new();
    private DateTime? _cacheStamp;
    private bool _loaded;
    private string? _loadError;

    public ScriptDefinitionStore(string filePath, ILogger<ScriptDefinitionStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public string FilePath => _filePath;

    public string? LoadError
    {
        get
        {
            lock (_lock)
            {
                Refresh();
                return _loadError;
            }
        }
    }

    public IReadOnlyList<ScriptDefinition> GetAll()
    {
        lock (_lock)
        {
            Refresh();
            return _cache.Select(Clone).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public ScriptDefinition? Get(string id)
    {
        lock (_lock)
        {
            Refresh();
            var d = _cache.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return d == null ? null : Clone(d);
        }
    }

    public void Save(ScriptDefinition definition)
    {
        lock (_lock)
        {
            Refresh();
            EnsureWritable();
            var list = _cache.ToList();
            var idx = list.FindIndex(x => x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) list[idx] = Clone(definition);
            else list.Add(Clone(definition));
            Write(list);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            Refresh();
            EnsureWritable();
            var list = _cache.ToList();
            if (list.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Write(list);
            return true;
        }
    }

    internal byte[] ReadCurrentFile()
    {
        lock (_lock)
        {
            return File.Exists(_filePath)
                ? File.ReadAllBytes(_filePath)
                : JsonSerializer.SerializeToUtf8Bytes(new ScriptsFile(), ScriptJson.Options);
        }
    }

    internal void ReplaceFile(byte[] contents)
    {
        lock (_lock)
        {
            var definitions = ParseJson(Decode(contents));
            JsonFileBackup.TryCreate(_filePath, _logger);
            var tmp = _filePath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, contents);
                File.Move(tmp, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            _cache = definitions;
            _cacheStamp = File.GetLastWriteTimeUtc(_filePath);
            _loaded = true;
            _loadError = null;
        }
    }

    internal void Reload()
    {
        lock (_lock)
        {
            _loaded = false;
            Refresh();
        }
    }

    internal static void ValidateJson(string json) => _ = ParseJson(json);

    private void EnsureWritable()
    {
        if (_loadError != null)
            throw new InvalidOperationException(_loadError + " Bitte die Datei korrigieren — sie wird nicht überschrieben.");
    }

    private void Refresh()
    {
        DateTime? stamp = File.Exists(_filePath) ? File.GetLastWriteTimeUtc(_filePath) : null;
        if (_loaded && _cacheStamp == stamp) return;

        _loaded = true;
        _cacheStamp = stamp;
        _loadError = null;
        if (stamp == null)
        {
            _cache = new List<ScriptDefinition>();
            return;
        }

        try
        {
            _cache = ParseJson(File.ReadAllText(_filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _cache = new List<ScriptDefinition>();
            _loadError = $"'{_filePath}' konnte nicht gelesen werden: {ex.Message}";
            _logger.LogError(ex, "Script definitions {Path} could not be read", _filePath);
        }
    }

    private static List<ScriptDefinition> ParseJson(string json) => string.IsNullOrWhiteSpace(json)
        ? new List<ScriptDefinition>()
        : JsonSerializer.Deserialize<ScriptsFile>(json, ScriptJson.Options)?.Scripts ?? new List<ScriptDefinition>();

    private static string Decode(byte[] contents)
    {
        using var reader = new StreamReader(new MemoryStream(contents, writable: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void Write(List<ScriptDefinition> list)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new ScriptsFile { Scripts = list }, ScriptJson.Options);
        JsonFileBackup.TryCreate(_filePath, _logger);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
        _cache = list;
        _cacheStamp = File.GetLastWriteTimeUtc(_filePath);
    }

    private static ScriptDefinition Clone(ScriptDefinition d) =>
        JsonSerializer.Deserialize<ScriptDefinition>(JsonSerializer.Serialize(d, ScriptJson.Options), ScriptJson.Options)!;

    private sealed class ScriptsFile
    {
        public List<ScriptDefinition> Scripts { get; set; } = new();
    }
}
