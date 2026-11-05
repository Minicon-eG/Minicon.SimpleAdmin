using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.WebUI.Helpers;

/// <summary>
/// German labels for Windows Scheduled Tasks: task state, last run result (hex + plain text for the
/// well-known Task Scheduler / Win32 codes) and the cross-server mode. Tones map to <c>pill pill-{tone}</c>.
/// </summary>
internal static class ScheduledTaskDisplay
{
    private static readonly Dictionary<uint, string> KnownResults = new()
    {
        [0x0] = "Erfolgreich",
        [0x1] = "Fehler (Exit-Code 1)",
        [0x2] = "Datei nicht gefunden (Exit-Code 2)",
        [0x00041300] = "Bereit für die nächste Ausführung",
        [0x00041301] = "Wird gerade ausgeführt",
        [0x00041302] = "Task ist deaktiviert",
        [0x00041303] = "Wurde noch nie ausgeführt",
        [0x00041304] = "Keine weiteren Ausführungen geplant",
        [0x00041306] = "Vom Benutzer beendet",
        [0x00041307] = "Keine gültigen Trigger",
        [0x00041325] = "In der Warteschlange",
        [0x8004130F] = "Anmeldedaten fehlen oder sind ungültig",
        [0x8004131F] = "Eine Instanz läuft bereits",
        [0x80070002] = "Datei nicht gefunden",
        [0x80070005] = "Zugriff verweigert",
        [0x8007010B] = "Ungültiges Startverzeichnis",
        [0x800700C1] = "Keine gültige Anwendung",
        [0x800704DD] = "Benutzer ist nicht angemeldet",
        [0x800710E0] = "Ausführung abgelehnt (Bedingungen/Richtlinie)",
        [0xC000013A] = "Abgebrochen (Strg+C / Herunterfahren)"
    };

    /// <summary>Result code as hex, e.g. <c>0x0</c>, <c>0x41301</c>, <c>0x80070005</c>; "—" when unknown.</summary>
    public static string ResultHex(int? code) => code.HasValue ? $"0x{unchecked((uint)code.Value):X}" : "—";

    /// <summary>German plain text for well-known result codes, null for others.</summary>
    public static string? ResultText(int? code) =>
        code.HasValue && KnownResults.TryGetValue(unchecked((uint)code.Value), out var text) ? text : null;

    /// <summary>Hex code → plain text, for the live state rendered in the browser.</summary>
    public static Dictionary<string, string> ResultTexts() =>
        KnownResults.ToDictionary(kv => $"0x{kv.Key:X}", kv => kv.Value);

    public static bool IsRunning(ScheduledTaskInfo task) =>
        string.Equals(task.State, "Running", StringComparison.OrdinalIgnoreCase);

    public static string StateLabel(ScheduledTaskInfo task) => task.State?.ToLowerInvariant() switch
    {
        "running" => "Läuft",
        "queued" => "In Warteschlange",
        "disabled" => "Deaktiviert",
        "ready" => task.Enabled ? "Aktiv" : "Deaktiviert",
        _ => task.Enabled ? "Aktiv" : "Unbekannt"
    };

    public static string StateTone(ScheduledTaskInfo task)
    {
        if (IsRunning(task) || string.Equals(task.State, "Queued", StringComparison.OrdinalIgnoreCase)) return "info";
        return task.Enabled ? "ok" : "muted";
    }

    public static string StateIcon(ScheduledTaskInfo task)
    {
        if (IsRunning(task)) return "bi-play-circle-fill";
        return task.Enabled ? "bi-check-circle-fill" : "bi-pause-circle";
    }

    /// <summary>Tone of the last result: failure from the worker evaluation, otherwise ok/muted.</summary>
    public static string ResultTone(ScheduledTaskInfo task)
    {
        if (task.LastRunFailed) return task.FailureIsCritical ? "crit" : "warn";
        return task.LastTaskResult.HasValue ? "ok" : "muted";
    }

    public static bool HasFinding(ScheduledTaskInfo task) => task.LastRunFailed || task.RuntimeExceeded || task.MissedRun;

    public static string ModeLabel(ScheduledTaskMode mode) => mode == ScheduledTaskMode.SingleActive
        ? "Nur auf einem Server aktiv"
        : "Auf allen Servern";
}
