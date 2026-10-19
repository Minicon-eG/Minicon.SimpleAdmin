using System.Globalization;

namespace Minicon.SimpleAdmin.WebUI.Hosting;

/// <summary>
/// Parsed options for the install / uninstall / start / stop subcommands.
/// </summary>
public sealed class InstallerOptions
{
    public const string DefaultServiceName = "SimpleAdminWebUI";
    public const string DefaultDisplayName = "Minicon SimpleAdmin WebUI";
    public const string DefaultDescription = "Admin-UI für die Minicon SimpleAdmin Monitoring-Plattform.";
    public const int DefaultPort = 5080;
    public const string DefaultKeysDirectory = @"C:\ProgramData\SimpleAdmin\dp-keys";
    public const string UrlAclTemplate = "http://+:{0}/";

    public string ServiceName { get; set; } = DefaultServiceName;
    public string DisplayName { get; set; } = DefaultDisplayName;
    public string Description { get; set; } = DefaultDescription;
    public int Port { get; set; } = DefaultPort;
    public string? Account { get; set; }
    public string? Password { get; set; }
    public string KeysDirectory { get; set; } = DefaultKeysDirectory;
    public bool NonInteractive { get; set; }
    public bool Purge { get; set; }

    /// <summary>
    /// The Windows principal URL-ACLs and ACLs are granted to. Defaults to
    /// <c>NT AUTHORITY\SYSTEM</c> when no explicit <c>--account</c> was given.
    /// </summary>
    public string EffectivePrincipal => string.IsNullOrWhiteSpace(Account) ? @"NT AUTHORITY\SYSTEM" : Account!;

    public string UrlAcl => string.Format(CultureInfo.InvariantCulture, UrlAclTemplate, Port);

    /// <summary>
    /// Parses the trailing arguments after an <c>install</c>/<c>uninstall</c>/<c>start</c>/<c>stop</c>
    /// subcommand. Supported flags: <c>--port=5080</c>, <c>--account=DOMAIN\u</c>,
    /// <c>--password=pw</c>, <c>--service-name=…</c>, <c>--display-name=…</c>,
    /// <c>--description=…</c>, <c>--keys-dir=…</c>, <c>--non-interactive</c>, <c>--purge</c>.
    /// </summary>
    public static InstallerOptions Parse(IEnumerable<string> args)
    {
        var o = new InstallerOptions();
        foreach (var raw in args)
        {
            var (key, value) = SplitFlag(raw);
            switch (key)
            {
                case "--port":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p is <= 0 or > 65535)
                        throw new ArgumentException($"Ungültiger Port: '{value}'");
                    o.Port = p;
                    break;
                case "--account":
                    o.Account = value;
                    break;
                case "--password":
                    o.Password = value;
                    break;
                case "--service-name":
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException("--service-name darf nicht leer sein.");
                    o.ServiceName = value!;
                    break;
                case "--display-name":
                    if (!string.IsNullOrWhiteSpace(value))
                        o.DisplayName = value!;
                    break;
                case "--description":
                    if (!string.IsNullOrWhiteSpace(value))
                        o.Description = value!;
                    break;
                case "--keys-dir":
                    if (!string.IsNullOrWhiteSpace(value))
                        o.KeysDirectory = value!;
                    break;
                case "--non-interactive":
                    o.NonInteractive = true;
                    break;
                case "--purge":
                    o.Purge = true;
                    break;
                default:
                    throw new ArgumentException($"Unbekanntes Argument: '{raw}'");
            }
        }
        return o;
    }

    private static (string Key, string? Value) SplitFlag(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Leeres Argument.");
        var eq = raw.IndexOf('=');
        return eq < 0 ? (raw, null) : (raw[..eq], raw[(eq + 1)..]);
    }
}
