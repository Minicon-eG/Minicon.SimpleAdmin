using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Minicon.SimpleAdmin.WebUI.Hosting;

/// <summary>
/// Installer for running the WebUI as a Windows service — without IIS. Covers
/// service registration (<c>sc.exe</c>), URL-ACL (<c>netsh http add urlacl</c>),
/// a firewall rule (<c>netsh advfirewall</c>) and the DataProtection keys
/// directory ACL (<c>icacls</c>). Every external invocation is routed through
/// <see cref="IProcessRunner"/> so the behaviour is testable.
/// </summary>
public static class ServiceInstaller
{
    /// <summary>
    /// Entry point called from <c>SimpleAdminWebUIHost.RunAsync</c> when the
    /// first positional argument is <c>install</c>/<c>uninstall</c>/<c>start</c>/<c>stop</c>.
    /// Returns the process exit code.
    /// </summary>
    public static int Dispatch(string command, string[] remainingArgs, string executablePath, TextWriter output, IProcessRunner? runner = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Fehler: Der Installer läuft nur unter Windows.");
            return 2;
        }

        runner ??= new DefaultProcessRunner();

        InstallerOptions opts;
        try
        {
            opts = InstallerOptions.Parse(remainingArgs);
        }
        catch (ArgumentException ex)
        {
            output.WriteLine($"Argumentfehler: {ex.Message}");
            output.WriteLine(Usage());
            return 2;
        }

        if (!IsAdministrator())
        {
            output.WriteLine("Fehler: Der Installer benötigt Administrator-Rechte. Bitte Eingabeaufforderung als Administrator starten.");
            return 1;
        }

        try
        {
            return command.ToLowerInvariant() switch
            {
                "install" => Install(opts, executablePath, output, runner),
                "uninstall" => Uninstall(opts, output, runner),
                "start" => StartService(opts, output, runner),
                "stop" => StopService(opts, output, runner),
                _ => UnknownCommand(command, output),
            };
        }
        catch (Exception ex)
        {
            output.WriteLine($"Fehler: {ex.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static int Install(InstallerOptions opts, string executablePath, TextWriter output, IProcessRunner runner)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException($"Dienst-Executable nicht gefunden: '{executablePath}'.");

        if (!string.IsNullOrEmpty(opts.Account) && string.IsNullOrEmpty(opts.Password) && !opts.NonInteractive)
        {
            output.WriteLine($"Passwort für Dienstkonto '{opts.Account}':");
            opts.Password = ReadPassword();
        }
        if (!string.IsNullOrEmpty(opts.Account) && string.IsNullOrEmpty(opts.Password) && opts.NonInteractive)
            throw new InvalidOperationException("--password muss im Non-Interactive-Modus gesetzt sein, wenn --account angegeben ist.");

        output.WriteLine($"[1/5] Dienst '{opts.ServiceName}' anlegen …");
        CreateService(opts, executablePath, output, runner);

        output.WriteLine($"[2/5] URL-ACL für {opts.UrlAcl} auf '{opts.EffectivePrincipal}' setzen …");
        ReserveUrlAcl(opts, output, runner);

        output.WriteLine($"[3/5] Firewall-Regel 'SimpleAdmin WebUI ({opts.Port})' anlegen …");
        OpenFirewall(opts, output, runner);

        output.WriteLine($"[4/5] DataProtection-Keys-Ordner '{opts.KeysDirectory}' vorbereiten …");
        PrepareKeysDirectory(opts, output, runner);

        output.WriteLine($"[5/5] Dienst starten …");
        var start = runner.Run("sc.exe", new[] { "start", opts.ServiceName });
        output.WriteLine(start.StdOut);
        if (!start.Success)
        {
            output.WriteLine(start.StdErr);
            output.WriteLine("Hinweis: Der Dienst wurde angelegt, konnte aber nicht gestartet werden. Prüfen Sie das Ereignisprotokoll.");
            return 1;
        }

        output.WriteLine($"Fertig. WebUI erreichbar unter http://<host>:{opts.Port}/");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    internal static int Uninstall(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        output.WriteLine($"[1/4] Dienst '{opts.ServiceName}' stoppen (sofern aktiv) …");
        var stop = runner.Run("sc.exe", new[] { "stop", opts.ServiceName });
        output.WriteLine(stop.StdOut);
        // Ignore exit code - service may already be stopped.

        output.WriteLine($"[2/4] URL-ACL {opts.UrlAcl} entfernen …");
        var delAcl = runner.Run("netsh", new[] { "http", "delete", "urlacl", $"url={opts.UrlAcl}" });
        output.WriteLine(delAcl.StdOut);

        output.WriteLine($"[3/4] Firewall-Regel entfernen …");
        var delFw = runner.Run("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name=SimpleAdmin WebUI ({opts.Port})" });
        output.WriteLine(delFw.StdOut);

        output.WriteLine($"[4/4] Dienst entfernen …");
        var delSvc = runner.Run("sc.exe", new[] { "delete", opts.ServiceName });
        output.WriteLine(delSvc.StdOut);
        if (!delSvc.Success)
            output.WriteLine(delSvc.StdErr);

        if (opts.Purge && Directory.Exists(opts.KeysDirectory))
        {
            output.WriteLine($"--purge: Keys-Ordner '{opts.KeysDirectory}' wird gelöscht.");
            Directory.Delete(opts.KeysDirectory, recursive: true);
        }
        else
        {
            output.WriteLine($"Hinweis: Keys-Ordner '{opts.KeysDirectory}' bleibt erhalten (laufende Cookies/Sessions). Mit --purge entfernen.");
        }

        return delSvc.Success ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    internal static int StartService(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        var r = runner.Run("sc.exe", new[] { "start", opts.ServiceName });
        output.WriteLine(r.StdOut);
        if (!r.Success) output.WriteLine(r.StdErr);
        return r.Success ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    internal static int StopService(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        var r = runner.Run("sc.exe", new[] { "stop", opts.ServiceName });
        output.WriteLine(r.StdOut);
        if (!r.Success) output.WriteLine(r.StdErr);
        return r.Success ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateService(InstallerOptions opts, string executablePath, TextWriter output, IProcessRunner runner)
    {
        // sc.exe wants `optName=` and the value as TWO separate argv tokens
        // ("start=" + "auto"), not one. Passing "start= auto" as a single
        // ArgumentList entry makes Windows quote the whole thing and sc.exe
        // rejects it with "Invalid start= field". Keep them split here.
        // ArgumentList will quote the path itself if it contains spaces; sc.exe
        // sees exactly the raw path string as the binPath value.
        var args = new List<string>
        {
            "create", opts.ServiceName,
            "binPath=", executablePath,
            "start=", "auto",
            "DisplayName=", opts.DisplayName
        };
        if (!string.IsNullOrEmpty(opts.Account))
        {
            args.Add("obj=");
            args.Add(opts.Account!);
            if (!string.IsNullOrEmpty(opts.Password))
            {
                args.Add("password=");
                args.Add(opts.Password!);
            }
        }

        var create = runner.Run("sc.exe", args);
        output.WriteLine(create.StdOut);
        if (!create.Success)
            throw new InvalidOperationException($"sc create fehlgeschlagen: {create.StdErr}".TrimEnd());

        var desc = runner.Run("sc.exe", new[] { "description", opts.ServiceName, opts.Description });
        if (!desc.Success) output.WriteLine(desc.StdErr);
    }

    [SupportedOSPlatform("windows")]
    private static void ReserveUrlAcl(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        // Remove any existing reservation (ignore result) and recreate for idempotency.
        runner.Run("netsh", new[] { "http", "delete", "urlacl", $"url={opts.UrlAcl}" });
        var r = runner.Run("netsh", new[] { "http", "add", "urlacl", $"url={opts.UrlAcl}", $"user={opts.EffectivePrincipal}" });
        output.WriteLine(r.StdOut);
        if (!r.Success)
            throw new InvalidOperationException($"netsh http add urlacl fehlgeschlagen: {r.StdErr}".TrimEnd());
    }

    [SupportedOSPlatform("windows")]
    private static void OpenFirewall(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        var ruleName = $"SimpleAdmin WebUI ({opts.Port})";
        // Idempotent: delete-if-present, then add.
        runner.Run("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleName}" });
        var r = runner.Run("netsh", new[]
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={ruleName}", "dir=in", "action=allow", "protocol=TCP",
            $"localport={opts.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        });
        output.WriteLine(r.StdOut);
        if (!r.Success)
            throw new InvalidOperationException($"Firewall-Regel konnte nicht angelegt werden: {r.StdErr}".TrimEnd());
    }

    [SupportedOSPlatform("windows")]
    private static void PrepareKeysDirectory(InstallerOptions opts, TextWriter output, IProcessRunner runner)
    {
        Directory.CreateDirectory(opts.KeysDirectory);
        var r = runner.Run("icacls", new[]
        {
            opts.KeysDirectory,
            "/grant", $"{opts.EffectivePrincipal}:(OI)(CI)M",
            "/T", "/C"
        });
        output.WriteLine(r.StdOut);
        if (!r.Success)
            throw new InvalidOperationException($"icacls fehlgeschlagen: {r.StdErr}".TrimEnd());
    }

    private static int UnknownCommand(string command, TextWriter output)
    {
        output.WriteLine($"Unbekanntes Kommando: '{command}'.");
        output.WriteLine(Usage());
        return 2;
    }

    internal static string Usage() =>
        """
        Verwendung:
          <exe> install   [--port=5080] [--account=DOMAIN\u] [--password=pw]
                          [--service-name=SimpleAdminWebUI] [--display-name=...]
                          [--description=...] [--keys-dir=C:\ProgramData\SimpleAdmin\dp-keys]
                          [--non-interactive]
          <exe> uninstall [--service-name=...] [--port=5080] [--purge]
          <exe> start     [--service-name=...]
          <exe> stop      [--service-name=...]
        """;

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ReadPassword()
    {
        var pw = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && pw.Length > 0) { pw.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) pw.Append(key.KeyChar);
        }
        Console.WriteLine();
        return pw.ToString();
    }
}
