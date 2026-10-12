using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// One validated argument for a script run.
/// </summary>
/// <param name="Kind">s = string, a = string[], b = bool, i = integer, n = number.</param>
internal sealed record ScriptArgument(string Name, char Kind, IReadOnlyList<string> Values)
{
    /// <summary>Value as shown in the run details (lists joined with ", ").</summary>
    public string Display => Kind switch
    {
        'b' => Values[0] == "1" ? "$true" : "$false",
        'a' => string.Join(", ", Values),
        _ => Values.Count > 0 ? Values[0] : string.Empty
    };

    /// <summary>The argument as it would be typed in PowerShell (see <see cref="ScriptArgumentBuilder.FormatCommandLine"/>).</summary>
    public string ToPowerShell() => Kind switch
    {
        'b' => Values[0] == "1" ? $"-{Name}" : $"-{Name}:$false",
        'a' => $"-{Name} @({string.Join(", ", Values.Select(ScriptArgumentBuilder.QuoteForDisplay))})",
        'i' or 'n' => $"-{Name} {Values[0]}",
        _ => $"-{Name} {ScriptArgumentBuilder.QuoteForDisplay(Values[0])}"
    };
}

/// <summary>
/// Validates the values entered in the run form against a <see cref="ScriptDefinition"/> and
/// transports them injection-free to PowerShell: the values never become part of a command
/// line — they are Base64-encoded into an environment variable and splatted by a fixed
/// bootstrap script (<see cref="Bootstrap"/>).
/// </summary>
internal static class ScriptArgumentBuilder
{
    internal const string ArgsVariable = "SIMPLEADMIN_SCRIPT_ARGS";
    internal const string PathVariable = "SIMPLEADMIN_SCRIPT_PATH";

    private static readonly Regex ValidName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Fixed PowerShell bootstrap: decodes the arguments, splats them into the script and
    /// propagates the exit code. Contains no user input.
    /// </summary>
    internal const string Bootstrap = """
        $ProgressPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
        if ($PSVersionTable.PSVersion.Major -ge 7 -and $PSStyle) { $PSStyle.OutputRendering = 'PlainText' }
        $__saArgs = @{}
        $__saDecode = { param($b) [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($b)) }
        foreach ($__saLine in ([string]$env:SIMPLEADMIN_SCRIPT_ARGS -split "`n")) {
            if (-not $__saLine) { continue }
            $__saParts = $__saLine -split "`t"
            $__saName = $__saParts[0]
            $__saData = if ($__saParts.Count -gt 2) { $__saParts[2] } else { '' }
            switch ($__saParts[1]) {
                's' { $__saArgs[$__saName] = & $__saDecode $__saData }
                'a' { $__saArgs[$__saName] = [string[]]@($__saData -split ',' | Where-Object { $_ } | ForEach-Object { & $__saDecode $_ }) }
                'b' { $__saArgs[$__saName] = ($__saData -eq '1') }
                'i' { $__saArgs[$__saName] = [long]::Parse($__saData, [System.Globalization.CultureInfo]::InvariantCulture) }
                'n' { $__saArgs[$__saName] = [double]::Parse($__saData, [System.Globalization.CultureInfo]::InvariantCulture) }
            }
        }
        $__saScript = $env:SIMPLEADMIN_SCRIPT_PATH
        Remove-Item Env:SIMPLEADMIN_SCRIPT_ARGS, Env:SIMPLEADMIN_SCRIPT_PATH -ErrorAction SilentlyContinue
        $global:LASTEXITCODE = 0
        try {
            & $__saScript @__saArgs
        } catch {
            [Console]::Error.WriteLine(($_ | Out-String).Trim())
            exit 1
        }
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        exit 0
        """;

    /// <summary>Bootstrap as UTF-16LE Base64 for <c>-EncodedCommand</c>.</summary>
    internal static string EncodedBootstrap { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap));

    /// <summary>
    /// Validates the form input. Returns the arguments to pass or a list of German error messages.
    /// </summary>
    public static (List<ScriptArgument> Arguments, List<string> Errors) Build(
        ScriptDefinition definition, IReadOnlyDictionary<string, List<string>> input)
    {
        var args = new List<ScriptArgument>();
        var errors = new List<string>();

        foreach (var p in definition.Parameters)
        {
            if (p.Hidden)
            {
                // A hidden mandatory parameter is never passed → PowerShell would fail on every start.
                if (p.Required) errors.Add($"'{p.DisplayLabel}' ist ein Pflichtparameter und darf nicht ausgeblendet sein (stattdessen „Fest“ mit Standardwert verwenden).");
                continue;
            }
            if (!ValidName.IsMatch(p.Name))
            {
                errors.Add($"Ungültiger Parametername '{p.Name}'.");
                continue;
            }

            var label = p.DisplayLabel;
            List<string> raw;
            if (p.Fixed)
                raw = p.Type == ScriptParameterType.List || p.AllowMultiple
                    ? ScriptParameterDefinition.SplitLines(p.DefaultValue)
                    : p.DefaultValue is null ? new List<string>() : new List<string> { p.DefaultValue };
            else
                raw = FindInput(input, p.Name) ?? new List<string>();

            raw = raw.Select(v => v?.Trim() ?? string.Empty).ToList();

            switch (p.Type)
            {
                case ScriptParameterType.Switch:
                {
                    var on = raw.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                          || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                                          || v == "1");
                    // Pass the value whenever leaving it out would not mean "off": the user turned on
                    // a switch, turned off a switch that defaults to $true (-X:$false), a [bool]
                    // parameter got a value, or the parameter is mandatory.
                    var mustPass = on
                                   || p.DefaultIsOn
                                   || p.Required
                                   || (PowerShellTypes.IsBoolean(p.PsType) && raw.Count > 0);
                    if (mustPass)
                        args.Add(new ScriptArgument(p.Name, 'b', new[] { on ? "1" : "0" }));
                    break;
                }

                case ScriptParameterType.List:
                case ScriptParameterType.Choice when p.AllowMultiple:
                {
                    var values = raw.SelectMany(SplitList).ToList();
                    if (values.Count == 0)
                    {
                        if (p.Required) errors.Add($"'{label}' ist ein Pflichtfeld.");
                        break;
                    }
                    var ok = true;
                    for (var i = 0; i < values.Count; i++)
                    {
                        if (p.Type == ScriptParameterType.Choice)
                        {
                            var match = p.Choices.FirstOrDefault(c => c.Equals(values[i], StringComparison.OrdinalIgnoreCase));
                            if (match == null) { errors.Add($"'{label}': '{values[i]}' ist kein zulässiger Wert."); ok = false; continue; }
                            values[i] = match;
                        }
                        if (!MatchesPattern(p, values[i])) { errors.Add($"'{label}': '{values[i]}' entspricht nicht dem Muster {p.Pattern}."); ok = false; }
                    }
                    if (ok) args.Add(new ScriptArgument(p.Name, 'a', values));
                    break;
                }

                default:
                {
                    var value = raw.FirstOrDefault(v => v.Length > 0);
                    if (value == null)
                    {
                        if (p.Required) errors.Add($"'{label}' ist ein Pflichtfeld.");
                        break;
                    }

                    var converted = ConvertScalar(p, value, out var kind, out var error);
                    if (error != null) { errors.Add($"'{label}': {error}"); break; }
                    if (!MatchesPattern(p, converted!)) { errors.Add($"'{label}': '{converted}' entspricht nicht dem Muster {p.Pattern}."); break; }
                    args.Add(new ScriptArgument(p.Name, kind, new[] { converted! }));
                    break;
                }
            }
        }

        return (args, errors);
    }

    /// <summary>
    /// Encodes the arguments for the bootstrap: one line per argument
    /// <c>name TAB kind TAB base64[,base64…]</c>.
    /// </summary>
    public static string Encode(IEnumerable<ScriptArgument> arguments)
    {
        var sb = new StringBuilder();
        foreach (var a in arguments)
        {
            var data = a.Kind switch
            {
                's' => B64(a.Values[0]),
                'a' => string.Join(",", a.Values.Select(B64)),
                _ => a.Values[0]
            };
            sb.Append(a.Name).Append('\t').Append(a.Kind).Append('\t').Append(data).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Human-readable equivalent call, e.g. <c>.\Copy.ps1 -SourceDate "2025-07-20" -DryRun</c>.
    /// Display only — execution never goes through a command line. The run form's live preview
    /// (Views/Scripts/Run.cshtml, <c>quote()</c>) follows the same rules.
    /// </summary>
    public static string FormatCommandLine(string scriptPath, IEnumerable<ScriptArgument> arguments)
    {
        var fileName = scriptPath[(scriptPath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return string.Join(" ", arguments.Select(a => a.ToPowerShell()).Prepend(".\\" + fileName));
    }

    /// <summary>Simple values in double quotes, everything else as single-quoted literal.</summary>
    internal static string QuoteForDisplay(string value) =>
        Regex.IsMatch(value, @"^[\w.:\\/-]+$") ? $"\"{value}\"" : Minicon.SimpleAdmin.Services.PowerShellLiteral.Quote(value);

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static List<string>? FindInput(IReadOnlyDictionary<string, List<string>> input, string name)
    {
        if (input.TryGetValue(name, out var v)) return v;
        foreach (var kv in input)
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Where(s => s.Length > 0);

    private static bool MatchesPattern(ScriptParameterDefinition p, string value)
    {
        if (string.IsNullOrWhiteSpace(p.Pattern)) return true;
        try
        {
            return Regex.IsMatch(value, p.Pattern!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return true; // invalid pattern configured — let PowerShell's own ValidatePattern decide
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? ConvertScalar(ScriptParameterDefinition p, string value, out char kind, out string? error)
    {
        kind = 's';
        error = null;
        var inv = CultureInfo.InvariantCulture;

        switch (p.Type)
        {
            case ScriptParameterType.Number:
            {
                var normalized = value.Replace(',', '.');
                if (!double.TryParse(normalized, NumberStyles.Float, inv, out var d))
                {
                    error = $"'{value}' ist keine Zahl.";
                    return null;
                }
                if (p.Min.HasValue && d < p.Min.Value) { error = $"Wert muss ≥ {p.Min.Value.ToString(inv)} sein."; return null; }
                if (p.Max.HasValue && d > p.Max.Value) { error = $"Wert muss ≤ {p.Max.Value.ToString(inv)} sein."; return null; }
                if (long.TryParse(normalized, NumberStyles.Integer, inv, out var l))
                {
                    kind = 'i';
                    return l.ToString(inv);
                }
                if (PowerShellTypes.IsInteger(p.PsType)) { error = $"'{value}' ist keine ganze Zahl."; return null; }
                kind = 'n';
                return d.ToString("R", inv);
            }

            case ScriptParameterType.Date:
                return ParseDate(value, p.EffectiveFormat, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "yyyyMMdd" }, "Datum", out error);

            case ScriptParameterType.Time:
                return ParseDate(value, p.EffectiveFormat, new[] { "HH:mm", "HH:mm:ss", "H:mm", "HHmm" }, "Uhrzeit", out error);

            case ScriptParameterType.DateTime:
                return ParseDate(value, p.EffectiveFormat,
                    new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm" },
                    "Zeitpunkt", out error);

            case ScriptParameterType.Choice:
            {
                var match = p.Choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (match == null && p.Choices.Count > 0) { error = $"'{value}' ist kein zulässiger Wert."; return null; }
                return match ?? value;
            }

            default:
                return value;
        }
    }

    private static string? ParseDate(string value, string outputFormat, string[] inputFormats, string what, out string? error)
    {
        error = null;
        var formats = inputFormats.Prepend(outputFormat).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToArray();
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString(outputFormat, CultureInfo.InvariantCulture);
        error = $"'{value}' ist kein gültiges {what} (erwartet {outputFormat}).";
        return null;
    }
}
