using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// JSON settings for the script runner's files (<c>scripts.json</c>, <c>script-runs/history.json</c>):
/// camelCase, indented, nulls omitted — the same conventions as config.json.
/// </summary>
internal static class ScriptJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Classification of (normalized) PowerShell type names, shared by the parser (control inference)
/// and the argument builder (value conversion).
/// </summary>
internal static class PowerShellTypes
{
    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "int16", "int32", "int64", "long", "short", "byte", "sbyte",
        "uint", "uint16", "uint32", "uint64", "ulong", "ushort"
    };

    private static readonly HashSet<string> FloatTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "double", "single", "float", "decimal"
    };

    public static bool IsInteger(string? type) => type != null && IntegerTypes.Contains(type);

    public static bool IsNumber(string? type) => IsInteger(type) || (type != null && FloatTypes.Contains(type));

    public static bool IsBoolean(string? type) => type is "bool" or "boolean";
}
