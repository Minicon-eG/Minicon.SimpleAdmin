using System.Globalization;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Converts stored values into what the HTML inputs expect (date → yyyy-MM-dd, time → HH:mm,
/// datetime-local → yyyy-MM-ddTHH:mm).
/// </summary>
internal static class ScriptFormValues
{
    public static List<string> Initial(ScriptParameterDefinition p, IReadOnlyDictionary<string, List<string>>? previous)
    {
        if (previous != null)
        {
            var match = previous.FirstOrDefault(kv => kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null) return match.Value.Select(v => ToInputValue(p, v)).ToList();
        }

        if (p.DefaultValue == null) return new List<string>();
        if (p.Type == ScriptParameterType.List || p.AllowMultiple)
            return ScriptParameterDefinition.SplitLines(p.DefaultValue);
        return new List<string> { ToInputValue(p, p.DefaultValue) };
    }

    public static string ToInputValue(ScriptParameterDefinition p, string value)
    {
        string[] formats;
        string target;
        switch (p.Type)
        {
            case ScriptParameterType.Date:
                formats = new[] { "yyyy-MM-dd", p.EffectiveFormat, "dd.MM.yyyy", "yyyyMMdd" };
                target = "yyyy-MM-dd";
                break;
            case ScriptParameterType.Time:
                formats = new[] { "HH:mm", p.EffectiveFormat, "HH:mm:ss", "H:mm", "HHmm" };
                target = "HH:mm";
                break;
            case ScriptParameterType.DateTime:
                formats = new[] { "yyyy-MM-ddTHH:mm", p.EffectiveFormat, "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm" };
                target = "yyyy-MM-ddTHH:mm";
                break;
            default:
                return value;
        }
        return DateTime.TryParseExact(value, formats.Distinct().ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString(target, CultureInfo.InvariantCulture)
            : value;
    }

    public static string TypeLabel(ScriptParameterType type) => type switch
    {
        ScriptParameterType.Text => "Text",
        ScriptParameterType.Number => "Zahl",
        ScriptParameterType.Date => "Datum",
        ScriptParameterType.Time => "Uhrzeit",
        ScriptParameterType.DateTime => "Datum + Uhrzeit",
        ScriptParameterType.Switch => "Schalter (Ja/Nein)",
        ScriptParameterType.Choice => "Auswahlliste",
        ScriptParameterType.List => "Liste (mehrere Werte)",
        ScriptParameterType.File => "Datei (Server)",
        ScriptParameterType.Folder => "Ordner (Server)",
        _ => type.ToString()
    };
}
