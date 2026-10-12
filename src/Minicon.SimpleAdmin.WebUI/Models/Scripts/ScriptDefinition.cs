using System.Globalization;
using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.WebUI.Models.Scripts;

/// <summary>
/// A PowerShell script that can be started from the WebUI ("Skripte"). Stored in
/// <c>scripts.json</c> next to config.json — the worker never reads it.
/// </summary>
public sealed class ScriptDefinition
{
    /// <summary>Stable identifier (URL-safe).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free text, prefilled from the script's <c>.SYNOPSIS</c>.</summary>
    public string? Description { get; set; }

    /// <summary>Full path of the .ps1 file on the WebUI host.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Working directory of the process; empty = directory of the script.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Maximum runtime in minutes; 0 = unlimited.</summary>
    public int TimeoutMinutes { get; set; }

    /// <summary>Whether several runs of this script may run at the same time (default: no).</summary>
    public bool AllowParallelRuns { get; set; }

    /// <summary>Parameters of the script (read from the param() block, adjustable in the UI).</summary>
    public List<ScriptParameterDefinition> Parameters { get; set; } = new();

    /// <summary>When the parameters were last read from the script file.</summary>
    public DateTime? ParametersReadAt { get; set; }
}

/// <summary>
/// Input control rendered for a script parameter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptParameterType>))]
public enum ScriptParameterType
{
    Text,
    Number,
    Date,
    Time,
    DateTime,
    Switch,
    Choice,
    List,
    File,
    Folder
}

/// <summary>
/// One parameter of a <see cref="ScriptDefinition"/>.
/// </summary>
public sealed class ScriptParameterDefinition
{
    /// <summary>PowerShell parameter name (without <c>$</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Label shown in the run form; empty = derived from the name.</summary>
    public string? Label { get; set; }

    /// <summary>Help text (from <c>.PARAMETER</c> / <c>HelpMessage</c>).</summary>
    public string? Description { get; set; }

    /// <summary>Input control.</summary>
    public ScriptParameterType Type { get; set; } = ScriptParameterType.Text;

    /// <summary>PowerShell type as declared in the script (e.g. <c>string[]</c>, <c>int</c>, <c>switch</c>); informational + used for value conversion.</summary>
    public string? PsType { get; set; }

    /// <summary>Mandatory parameter.</summary>
    public bool Required { get; set; }

    /// <summary>Default value (lists: one entry per line).</summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Default value as declared in the script when the parameters were last read. Lets
    /// "Parameter neu einlesen" tell an admin override apart from a changed script default.
    /// </summary>
    public string? ScriptDefaultValue { get; set; }

    /// <summary>Non-literal default expression of the script (informational only).</summary>
    public string? DefaultExpression { get; set; }

    /// <summary>Allowed values for <see cref="ScriptParameterType.Choice"/> (from <c>[ValidateSet()]</c>).</summary>
    public List<string> Choices { get; set; } = new();

    /// <summary>Choice with several selectable values (array parameter with ValidateSet).</summary>
    public bool AllowMultiple { get; set; }

    /// <summary>.NET format the value is passed in for Date/Time/DateTime (default <c>yyyy-MM-dd</c> / <c>HH:mm</c> / <c>yyyy-MM-dd HH:mm</c>).</summary>
    public string? Format { get; set; }

    /// <summary>Regex the value must match (from <c>[ValidatePattern()]</c>).</summary>
    public string? Pattern { get; set; }

    /// <summary>Lower bound for numbers (from <c>[ValidateRange()]</c>).</summary>
    public double? Min { get; set; }

    /// <summary>Upper bound for numbers (from <c>[ValidateRange()]</c>).</summary>
    public double? Max { get; set; }

    /// <summary>Value is fixed by the admin (<see cref="DefaultValue"/>) and cannot be changed when starting.</summary>
    public bool Fixed { get; set; }

    /// <summary>Parameter is not passed at all and not shown in the run form.</summary>
    public bool Hidden { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? HumanizeName(Name) : Label!;

    [JsonIgnore]
    public string ChoicesText
    {
        get => string.Join(Environment.NewLine, Choices);
        set => Choices = SplitLines(value);
    }

    /// <summary>Form binding for <see cref="Min"/>, culture-invariant (a de-DE server would read "0.5" as 5).</summary>
    [JsonIgnore]
    public string? MinText
    {
        get => Min?.ToString(CultureInfo.InvariantCulture);
        set => Min = ParseInvariant(value);
    }

    /// <summary>Form binding for <see cref="Max"/>, culture-invariant.</summary>
    [JsonIgnore]
    public string? MaxText
    {
        get => Max?.ToString(CultureInfo.InvariantCulture);
        set => Max = ParseInvariant(value);
    }

    /// <summary>Whether the switch is on unless the user turns it off.</summary>
    [JsonIgnore]
    public bool DefaultIsOn => string.Equals(DefaultValue, "true", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string EffectiveFormat => !string.IsNullOrWhiteSpace(Format) ? Format! : Type switch
    {
        ScriptParameterType.Date => "yyyy-MM-dd",
        ScriptParameterType.Time => "HH:mm",
        ScriptParameterType.DateTime => "yyyy-MM-dd HH:mm",
        _ => string.Empty
    };

    /// <summary>"WaitBetweenFilesSeconds" → "Wait Between Files Seconds".</summary>
    internal static string HumanizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                sb.Append(' ');
            sb.Append(c == '_' ? ' ' : c);
        }
        return sb.ToString();
    }

    internal static double? ParseInvariant(string? value) =>
        double.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static List<string> SplitLines(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
}
