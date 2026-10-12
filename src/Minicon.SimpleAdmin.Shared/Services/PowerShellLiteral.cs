using System.Text;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// PowerShell string-literal rules shared by the worker (EventLogChecker query) and the WebUI
/// (script parameter parser). Keeping them in one place matters: if the quoting and the parsing
/// disagree on which characters end a single-quoted string, input can break out of a literal.
/// </summary>
public static class PowerShellLiteral
{
    /// <summary>
    /// Characters PowerShell treats as a single quote: ' plus the typographic variants
    /// U+2018 ‘, U+2019 ’, U+201A ‚ and U+201B ‛.
    /// </summary>
    public static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';

    /// <summary>
    /// Wraps <paramref name="value"/> in single quotes for safe embedding into a PowerShell script:
    /// every single-quote variant is doubled (PowerShell's escape inside '…'), control characters are dropped.
    /// </summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            if (IsSingleQuote(c)) sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
