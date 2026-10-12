using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Persists the script definitions in <c>scripts.json</c> next to config.json (atomic write:
/// temp file + move). The file is re-read when it changes on disk, so manual edits are picked
/// up instead of being overwritten by the next save. An unreadable file is reported via
/// <see cref="LoadError"/> and never overwritten.
/// </summary>
public sealed class ScriptDefinitionStore
{
    private readonly string _filePath;
    private readonly ILogger<ScriptDefinitionStore> _logger;
    private readonly object _lock = new();
    private List<ScriptDefinition> _cache = new();
    private DateTime? _cacheStamp;
    private bool _loaded;

    public ScriptDefinitionStore(string filePath, ILogger<ScriptDefinitionStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public string FilePath => _filePath;

    /// <summary>German error when scripts.json could not be read; saving is blocked meanwhile.</summary>
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

    private string? _loadError;

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

    /// <summary>Inserts or replaces a definition (by <see cref="ScriptDefinition.Id"/>).</summary>
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

    private void EnsureWritable()
    {
        if (_loadError != null)
            throw new InvalidOperationException(_loadError + " Bitte die Datei korrigieren — sie wird nicht überschrieben.");
    }

    /// <summary>Reloads the cache when the file's write time changed (a cheap stat per call).</summary>
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
            var json = File.ReadAllText(_filePath);
            _cache = string.IsNullOrWhiteSpace(json)
                ? new List<ScriptDefinition>()
                : JsonSerializer.Deserialize<ScriptsFile>(json, ScriptJson.Options)?.Scripts ?? new List<ScriptDefinition>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _cache = new List<ScriptDefinition>();
            _loadError = $"'{_filePath}' konnte nicht gelesen werden: {ex.Message}";
            _logger.LogError(ex, "Script definitions {Path} could not be read", _filePath);
        }
    }

    private void Write(List<ScriptDefinition> list)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new ScriptsFile { Scripts = list }, ScriptJson.Options);
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
