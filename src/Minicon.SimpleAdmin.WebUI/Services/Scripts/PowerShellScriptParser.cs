using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Reads the script-level <c>param()</c> block and the comment-based help of a PowerShell
/// script without executing it (and without needing PowerShell on the WebUI host), and maps
/// every parameter to a sensible input control: <c>[switch]</c> → checkbox,
/// <c>[ValidateSet()]</c> → dropdown, <c>[int]</c> → number, <c>[string[]]</c> → list,
/// date/time/path parameters (detected by type, name and help text) → date/time picker and
/// server-side file/folder chooser.
/// </summary>
internal static class PowerShellScriptParser
{
    private static readonly HashSet<string> HelpKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYNOPSIS", "DESCRIPTION", "PARAMETER", "EXAMPLE", "INPUTS", "OUTPUTS", "NOTES", "LINK",
        "COMPONENT", "ROLE", "FUNCTIONALITY", "FORWARDHELPTARGETNAME", "FORWARDHELPCATEGORY",
        "REMOTEHELPRUNSPACE", "EXTERNALHELP"
    };

    private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "int16", "int32", "int64", "long", "short", "byte", "sbyte", "uint", "uint16", "uint32",
        "uint64", "ulong", "ushort", "double", "single", "float", "decimal"
    };

    private static readonly Regex TimeName = new(@"(^|[a-z])Time([A-Z_]|$)", RegexOptions.Compiled);
    private static readonly Regex FolderName = new(@"(Folder|Directory|Dir([A-Z_]|$)|Verzeichnis|Ordner)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FileName = new(@"(File|Path|Datei|Pfad)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberLiteral = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    // ── Public entry points ────────────────────────────────────────────────

    /// <summary>
    /// Builds a complete <see cref="ScriptDefinition"/> for a script file.
    /// </summary>
    public static ScriptDefinition CreateDefinition(string path, string content)
    {
        var parsed = Parse(content);
        return new ScriptDefinition
        {
            // Split on both separators: Windows paths are also handled on a non-Windows host.
            Name = System.IO.Path.GetFileNameWithoutExtension(path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..]),
            Description = parsed.Synopsis ?? parsed.Description,
            Path = path,
            Parameters = parsed.Parameters.Select(ToDefinition).ToList(),
            ParametersReadAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Re-reads the parameters of a script while keeping what the admin adjusted
    /// (label, control type, format, fixed/hidden, default value) for parameters that still exist.
    /// Parameters removed from the script are dropped, new ones are added.
    /// </summary>
    public static List<ScriptParameterDefinition> MergeParameters(
        IReadOnlyList<ScriptParameterDefinition> existing, IReadOnlyList<ScriptParameterDefinition> fresh)
    {
        var result = new List<ScriptParameterDefinition>(fresh.Count);
        foreach (var f in fresh)
        {
            var old = existing.FirstOrDefault(e => e.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                f.Label = old.Label;
                f.Type = old.Type;
                f.Format = old.Format;
                f.Fixed = old.Fixed;
                f.Hidden = old.Hidden;
                f.AllowMultiple = old.AllowMultiple;
                if (old.DefaultValue != null) f.DefaultValue = old.DefaultValue;
                if (f.Choices.Count == 0) f.Choices = old.Choices;
                if (string.IsNullOrWhiteSpace(f.Pattern)) f.Pattern = old.Pattern;
                f.Min ??= old.Min;
                f.Max ??= old.Max;
                if (string.IsNullOrWhiteSpace(f.Description)) f.Description = old.Description;
            }
            result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// Parses the comment-based help and the script-level param() block.
    /// </summary>
    public static ParsedScript Parse(string content)
    {
        content ??= string.Empty;
        var result = new ParsedScript();
        var help = ParseHelp(content);
        result.Synopsis = help.Synopsis;
        result.Description = help.Description;

        var open = FindScriptParamBlock(content);
        if (open < 0) return result;
        result.HasParamBlock = true;

        var inner = ExtractBalanced(content, open, out _);
        foreach (var segment in SplitTopLevel(inner, ','))
        {
            var p = ParseParameterSegment(segment);
            if (p == null) continue;
            if (help.Parameters.TryGetValue(p.Name, out var desc)) p.HelpDescription = desc;
            result.Parameters.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Maps a parsed parameter to the UI definition (control type inference).
    /// </summary>
    public static ScriptParameterDefinition ToDefinition(ParsedParameter p)
    {
        var type = NormalizeType(p.TypeName);
        var isArray = type.EndsWith("[]", StringComparison.Ordinal);
        var baseType = isArray ? type[..^2] : type;
        var description = string.IsNullOrWhiteSpace(p.HelpDescription) ? p.HelpMessage : p.HelpDescription;
        var text = (description ?? string.Empty);

        var def = new ScriptParameterDefinition
        {
            Name = p.Name,
            Description = description,
            PsType = string.IsNullOrEmpty(type) ? null : type,
            Required = p.Mandatory,
            Pattern = p.ValidatePattern,
            Min = p.RangeMin,
            Max = p.RangeMax,
            DefaultExpression = p.DefaultExpression
        };

        if (p.ValidateSet.Count > 0)
        {
            def.Type = ScriptParameterType.Choice;
            def.Choices = p.ValidateSet.ToList();
            def.AllowMultiple = isArray;
        }
        else if (baseType is "switch" or "switchparameter" or "bool" or "boolean")
        {
            def.Type = ScriptParameterType.Switch;
        }
        else if (NumberTypes.Contains(baseType) && !isArray)
        {
            def.Type = ScriptParameterType.Number;
        }
        else if (isArray || baseType is "array" or "object[]")
        {
            def.Type = ScriptParameterType.List;
        }
        else if (baseType is "io.fileinfo" or "fileinfo")
        {
            def.Type = ScriptParameterType.File;
        }
        else if (baseType is "io.directoryinfo" or "directoryinfo")
        {
            def.Type = ScriptParameterType.Folder;
        }
        else if (baseType is "datetime" or "datetimeoffset")
        {
            def.Type = LooksLikeDateOnly(p.Name, text) ? ScriptParameterType.Date : ScriptParameterType.DateTime;
        }
        else if (baseType is "" or "string" or "object")
        {
            def.Type = InferStringControl(p.Name, text);
        }

        def.Format = DetectFormat(def.Type, text);

        if (def.Type == ScriptParameterType.Switch)
        {
            // A switch defaults to "off"; only an explicit $true default is worth storing.
            def.DefaultValue = p.DefaultValue == "true" ? "true" : null;
        }
        else
        {
            def.DefaultValue = p.DefaultValue;
        }

        return def;
    }

    // ── Control type inference ─────────────────────────────────────────────

    private static ScriptParameterType InferStringControl(string name, string help)
    {
        if (help.Contains("YYYY-MM-DD", StringComparison.OrdinalIgnoreCase)
            || help.Contains("DD.MM.YYYY", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Date", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Date", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Datum", StringComparison.OrdinalIgnoreCase))
        {
            return help.Contains("HH:mm", StringComparison.Ordinal) ? ScriptParameterType.DateTime : ScriptParameterType.Date;
        }

        if (help.Contains("HH:mm", StringComparison.Ordinal) || TimeName.IsMatch(name)
            || name.Contains("Uhrzeit", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptParameterType.Time;
        }

        if (FolderName.IsMatch(name)) return ScriptParameterType.Folder;
        if (FileName.IsMatch(name))
        {
            // "SourcePath" documented as "Verzeichnis"/"Ordner"/"directory"/"folder" → folder chooser.
            return Regex.IsMatch(help, @"\b(Verzeichnis\w*|Ordner\w*|directory|folder)\b", RegexOptions.IgnoreCase)
                   && !Regex.IsMatch(help, @"\b(Datei\w*|file)\b", RegexOptions.IgnoreCase)
                ? ScriptParameterType.Folder
                : ScriptParameterType.File;
        }

        return ScriptParameterType.Text;
    }

    private static bool LooksLikeDateOnly(string name, string help) =>
        !TimeName.IsMatch(name) && !help.Contains("HH:mm", StringComparison.Ordinal);

    private static string? DetectFormat(ScriptParameterType type, string help)
    {
        switch (type)
        {
            case ScriptParameterType.Date:
                if (help.Contains("DD.MM.YYYY", StringComparison.OrdinalIgnoreCase)) return "dd.MM.yyyy";
                if (help.Contains("YYYYMMDD", StringComparison.OrdinalIgnoreCase)) return "yyyyMMdd";
                return null; // yyyy-MM-dd
            case ScriptParameterType.Time:
                if (help.Contains("HH:mm:ss", StringComparison.Ordinal)) return "HH:mm:ss";
                if (help.Contains("HHmm", StringComparison.Ordinal)) return "HHmm";
                return null; // HH:mm
            default:
                return null;
        }
    }

    private static string NormalizeType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        var t = Regex.Replace(typeName, @"\s+", "").ToLowerInvariant();
        if (t.StartsWith("system.", StringComparison.Ordinal)) t = t["system.".Length..];
        if (t.StartsWith("management.automation.", StringComparison.Ordinal)) t = t["management.automation.".Length..];
        var nullable = Regex.Match(t, @"^nullable\[(.+)\]$");
        if (nullable.Success) t = nullable.Groups[1].Value;
        return t switch
        {
            "int32" => "int",
            "int64" => "long",
            "boolean" => "bool",
            "switchparameter" => "switch",
            "string" or "string[]" => t,
            _ => t
        };
    }

    // ── param() parsing ────────────────────────────────────────────────────

    private static ParsedParameter? ParseParameterSegment(string segment)
    {
        var p = new ParsedParameter();
        var i = 0;
        string? name = null;
        while (i < segment.Length)
        {
            var skipped = SkipNonCode(segment, i);
            if (skipped > i) { i = skipped; continue; }
            var c = segment[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (name == null && c == '[')
            {
                var content = ExtractBalanced(segment, i, out var end).Trim();
                ApplyAttribute(p, content);
                i = end + 1;
                continue;
            }

            if (name == null && c == '$')
            {
                var start = ++i;
                if (i < segment.Length && segment[i] == '{')
                {
                    var close = segment.IndexOf('}', i);
                    if (close < 0) return null;
                    name = segment[(i + 1)..close];
                    i = close + 1;
                }
                else
                {
                    while (i < segment.Length && (char.IsLetterOrDigit(segment[i]) || segment[i] == '_')) i++;
                    name = segment[start..i];
                }
                continue;
            }

            if (name != null && c == '=')
            {
                var expr = StripComments(segment[(i + 1)..]).Trim();
                ApplyDefault(p, expr);
                break;
            }

            i++;
        }

        if (string.IsNullOrEmpty(name)) return null;
        p.Name = name;
        return p;
    }

    private static void ApplyAttribute(ParsedParameter p, string content)
    {
        var paren = content.IndexOf('(');
        if (paren < 0)
        {
            // Type literal such as [string], [string[]], [int], [switch].
            p.TypeName = content;
            return;
        }

        var attrName = content[..paren].Trim();
        var args = SplitTopLevel(ExtractBalanced(content, paren, out _), ',')
            .Select(a => StripComments(a).Trim())
            .Where(a => a.Length > 0)
            .ToList();

        switch (attrName.ToLowerInvariant())
        {
            case "parameter":
                foreach (var arg in args)
                {
                    var eq = FindTopLevel(arg, '=');
                    var key = (eq < 0 ? arg : arg[..eq]).Trim();
                    var value = eq < 0 ? "$true" : arg[(eq + 1)..].Trim();
                    if (key.Equals("Mandatory", StringComparison.OrdinalIgnoreCase))
                        p.Mandatory = !value.Equals("$false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    else if (key.Equals("HelpMessage", StringComparison.OrdinalIgnoreCase) && TryParseLiteral(value, out var hm))
                        p.HelpMessage = hm;
                }
                break;
            case "validateset":
                foreach (var arg in args)
                {
                    if (FindTopLevel(arg, '=') >= 0) continue; // IgnoreCase = $false etc.
                    if (TryParseLiteral(arg, out var v) && v != null) p.ValidateSet.Add(v);
                }
                break;
            case "validatepattern":
                if (args.Count > 0 && TryParseLiteral(args[0], out var pattern)) p.ValidatePattern = pattern;
                break;
            case "validaterange":
                if (args.Count >= 2)
                {
                    if (TryParseLiteral(args[0], out var min) && double.TryParse(min, NumberStyles.Float, CultureInfo.InvariantCulture, out var mn)) p.RangeMin = mn;
                    if (TryParseLiteral(args[1], out var max) && double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out var mx)) p.RangeMax = mx;
                }
                break;
        }
    }

    private static void ApplyDefault(ParsedParameter p, string expr)
    {
        if (expr.Length == 0) return;

        if (TryParseLiteral(expr, out var single))
        {
            p.DefaultValue = single;
            return;
        }

        var list = expr;
        if (list.StartsWith("@(", StringComparison.Ordinal) && list.EndsWith(')')) list = list[2..^1];
        else if (list.StartsWith('(') && list.EndsWith(')')) list = list[1..^1];
        else if (FindTopLevel(list, ',') < 0)
        {
            p.DefaultExpression = expr;
            return;
        }

        var values = new List<string>();
        foreach (var item in SplitTopLevel(list, ','))
        {
            var trimmed = StripComments(item).Trim();
            if (trimmed.Length == 0) continue;
            if (!TryParseLiteral(trimmed, out var v) || v == null)
            {
                p.DefaultExpression = expr;
                return;
            }
            values.Add(v);
        }
        p.DefaultValue = string.Join("\n", values);
    }

    /// <summary>
    /// Parses a PowerShell literal: quoted strings (without variable expansion), numbers,
    /// <c>$true</c>/<c>$false</c>/<c>$null</c> and bare words.
    /// </summary>
    internal static bool TryParseLiteral(string expr, out string? value)
    {
        value = null;
        expr = expr.Trim();
        if (expr.Length == 0) return false;

        if (expr.Length >= 2 && IsSingleQuote(expr[0]) && IsSingleQuote(expr[^1]))
        {
            var inner = expr[1..^1];
            if (HasUnescaped(inner, '\'')) return false;
            value = inner.Replace("''", "'");
            return true;
        }

        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
        {
            var inner = expr[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (c == '`' && i + 1 < inner.Length)
                {
                    var n = inner[++i];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => n });
                    continue;
                }
                if (c == '"' && i + 1 < inner.Length && inner[i + 1] == '"') { sb.Append('"'); i++; continue; }
                if (c == '"') return false;            // "a" + "b"
                if (c == '$' && i + 1 < inner.Length && (char.IsLetter(inner[i + 1]) || inner[i + 1] is '(' or '{' or '_'))
                    return false;                       // variable expansion → expression
                sb.Append(c);
            }
            value = sb.ToString();
            return true;
        }

        if (expr.Equals("$true", StringComparison.OrdinalIgnoreCase)) { value = "true"; return true; }
        if (expr.Equals("$false", StringComparison.OrdinalIgnoreCase)) { value = "false"; return true; }
        if (expr.Equals("$null", StringComparison.OrdinalIgnoreCase)) { value = null; return true; }
        if (NumberLiteral.IsMatch(expr)) { value = expr; return true; }
        if (Regex.IsMatch(expr, @"^[A-Za-z_][\w\-\.]*$")) { value = expr; return true; }
        return false;
    }

    private static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’';

    private static bool HasUnescaped(string inner, char quote)
    {
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != quote) continue;
            if (i + 1 < inner.Length && inner[i + 1] == quote) { i++; continue; }
            return true;
        }
        return false;
    }

    // ── Comment-based help ─────────────────────────────────────────────────

    private sealed class HelpInfo
    {
        public string? Synopsis;
        public string? Description;
        public readonly Dictionary<string, string> Parameters = new(StringComparer.OrdinalIgnoreCase);
    }

    private static HelpInfo ParseHelp(string content)
    {
        var help = new HelpInfo();
        var idx = 0;
        while ((idx = content.IndexOf("<#", idx, StringComparison.Ordinal)) >= 0)
        {
            var end = content.IndexOf("#>", idx + 2, StringComparison.Ordinal);
            if (end < 0) break;
            var block = content[(idx + 2)..end];
            idx = end + 2;
            if (!Regex.IsMatch(block, @"^\s*\.(SYNOPSIS|DESCRIPTION|PARAMETER)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                continue;

            string? keyword = null, arg = null;
            var buffer = new List<string>();
            void Flush()
            {
                if (keyword == null) return;
                var text = JoinHelpLines(buffer);
                switch (keyword.ToUpperInvariant())
                {
                    case "SYNOPSIS": help.Synopsis ??= text; break;
                    case "DESCRIPTION": help.Description ??= text; break;
                    case "PARAMETER" when !string.IsNullOrWhiteSpace(arg): help.Parameters[arg!.Trim()] = text; break;
                }
                buffer.Clear();
            }

            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var m = Regex.Match(line, @"^\s*\.([A-Za-z]+)\s*(.*)$");
                if (m.Success && HelpKeywords.Contains(m.Groups[1].Value))
                {
                    Flush();
                    keyword = m.Groups[1].Value;
                    arg = m.Groups[2].Value;
                    continue;
                }
                buffer.Add(line);
            }
            Flush();
            break;
        }
        return help;
    }

    private static string JoinHelpLines(List<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length == 0)
            {
                if (current.Count > 0) { paragraphs.Add(string.Join(" ", current)); current.Clear(); }
                continue;
            }
            current.Add(t);
        }
        if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
        return string.Join("\n", paragraphs);
    }

    // ── Low-level scanning (comments, strings, nesting) ────────────────────

    /// <summary>Index of the '(' of the script-level param block, or -1.</summary>
    internal static int FindScriptParamBlock(string s)
    {
        var depth = 0;
        var i = 0;
        while (i < s.Length)
        {
            var skipped = SkipNonCode(s, i);
            if (skipped > i) { i = skipped; continue; }
            var c = s[i];
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && (c == 'p' || c == 'P')
                     && string.Compare(s, i, "param", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                     && (i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or '-' or '$' or '.')))
            {
                var j = i + 5;
                while (j < s.Length)
                {
                    var sk = SkipNonCode(s, j);
                    if (sk > j) { j = sk; continue; }
                    if (char.IsWhiteSpace(s[j])) { j++; continue; }
                    break;
                }
                if (j < s.Length && s[j] == '(') return j;
            }
            i++;
        }
        return -1;
    }

    /// <summary>
    /// If <paramref name="i"/> starts a comment or string, returns the index after it; otherwise <paramref name="i"/>.
    /// </summary>
    internal static int SkipNonCode(string s, int i)
    {
        if (i >= s.Length) return i;
        var c = s[i];

        if (c == '<' && i + 1 < s.Length && s[i + 1] == '#')
        {
            var end = s.IndexOf("#>", i + 2, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }
        if (c == '#')
        {
            var end = s.IndexOf('\n', i);
            return end < 0 ? s.Length : end;
        }
        if (c == '@' && i + 1 < s.Length && (s[i + 1] == '\'' || s[i + 1] == '"'))
        {
            // Here-string: @' ... newline '@
            var rest = i + 2;
            while (rest < s.Length && s[rest] is ' ' or '\t' or '\r') rest++;
            if (rest < s.Length && s[rest] == '\n')
            {
                var terminator = "\n" + s[i + 1] + "@";
                var end = s.IndexOf(terminator, rest, StringComparison.Ordinal);
                return end < 0 ? s.Length : end + terminator.Length;
            }
            return i;
        }
        if (IsSingleQuote(c))
        {
            var j = i + 1;
            while (j < s.Length)
            {
                if (IsSingleQuote(s[j]))
                {
                    if (j + 1 < s.Length && IsSingleQuote(s[j + 1])) { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return s.Length;
        }
        if (c == '"')
        {
            var j = i + 1;
            while (j < s.Length)
            {
                if (s[j] == '`') { j += 2; continue; }
                if (s[j] == '"')
                {
                    if (j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return s.Length;
        }
        return i;
    }

    /// <summary>
    /// Returns the content between the bracket at <paramref name="openIndex"/> and its matching
    /// closing bracket (strings/comments aware). <paramref name="closeIndex"/> receives the index of the closing bracket.
    /// </summary>
    internal static string ExtractBalanced(string s, int openIndex, out int closeIndex)
    {
        var open = s[openIndex];
        var close = open switch { '(' => ')', '[' => ']', '{' => '}', _ => throw new ArgumentException("No bracket", nameof(openIndex)) };
        var depth = 0;
        var i = openIndex;
        while (i < s.Length)
        {
            var skipped = SkipNonCode(s, i);
            if (skipped > i) { i = skipped; continue; }
            if (s[i] == open) depth++;
            else if (s[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = i;
                    return s[(openIndex + 1)..i];
                }
            }
            i++;
        }
        closeIndex = s.Length - 1;
        return s[(openIndex + 1)..];
    }

    internal static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var i = 0;
        while (i < s.Length)
        {
            var skipped = SkipNonCode(s, i);
            if (skipped > i) { i = skipped; continue; }
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
            i++;
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static int FindTopLevel(string s, char ch)
    {
        var depth = 0;
        var i = 0;
        while (i < s.Length)
        {
            var skipped = SkipNonCode(s, i);
            if (skipped > i) { i = skipped; continue; }
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ch && depth == 0) return i;
            i++;
        }
        return -1;
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var skipped = SkipNonCode(s, i);
            if (skipped > i)
            {
                if (s[i] != '#' && !(s[i] == '<' && i + 1 < s.Length && s[i + 1] == '#'))
                    sb.Append(s, i, skipped - i); // keep strings
                i = skipped;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>Result of <see cref="PowerShellScriptParser.Parse"/>.</summary>
internal sealed class ParsedScript
{
    public string? Synopsis { get; set; }
    public string? Description { get; set; }
    public bool HasParamBlock { get; set; }
    public List<ParsedParameter> Parameters { get; } = new();
}

/// <summary>A raw parameter of the param() block.</summary>
internal sealed class ParsedParameter
{
    public string Name { get; set; } = string.Empty;
    public string? TypeName { get; set; }
    public bool Mandatory { get; set; }
    public string? HelpMessage { get; set; }
    public string? HelpDescription { get; set; }
    public List<string> ValidateSet { get; } = new();
    public string? ValidatePattern { get; set; }
    public double? RangeMin { get; set; }
    public double? RangeMax { get; set; }
    /// <summary>Literal default; list defaults are joined with "\n".</summary>
    public string? DefaultValue { get; set; }
    public string? DefaultExpression { get; set; }
}
