using System.Text.Json;
using System.Text.Json.Serialization;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Persists the script definitions in <c>scripts.json</c> next to config.json
/// (atomic write: temp file + move).
/// </summary>
public sealed class ScriptDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<ScriptDefinition>? _cache;

    public ScriptDefinitionStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public IReadOnlyList<ScriptDefinition> GetAll()
    {
        lock (_lock)
        {
            return Load().Select(Clone).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public ScriptDefinition? Get(string id)
    {
        lock (_lock)
        {
            var d = Load().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return d == null ? null : Clone(d);
        }
    }

    /// <summary>Inserts or replaces a definition (by <see cref="ScriptDefinition.Id"/>).</summary>
    public void Save(ScriptDefinition definition)
    {
        lock (_lock)
        {
            var list = Load().ToList();
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
            var list = Load().ToList();
            var removed = list.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Write(list);
            return true;
        }
    }

    private List<ScriptDefinition> Load()
    {
        if (_cache != null) return _cache;
        if (!File.Exists(_filePath)) return _cache = new List<ScriptDefinition>();
        var json = File.ReadAllText(_filePath);
        _cache = string.IsNullOrWhiteSpace(json)
            ? new List<ScriptDefinition>()
            : JsonSerializer.Deserialize<ScriptsFile>(json, JsonOptions)?.Scripts ?? new List<ScriptDefinition>();
        return _cache;
    }

    private void Write(List<ScriptDefinition> list)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new ScriptsFile { Scripts = list }, JsonOptions);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
        _cache = list;
    }

    private static ScriptDefinition Clone(ScriptDefinition d) =>
        JsonSerializer.Deserialize<ScriptDefinition>(JsonSerializer.Serialize(d, JsonOptions), JsonOptions)!;

    private sealed class ScriptsFile
    {
        public List<ScriptDefinition> Scripts { get; set; } = new();
    }
}
