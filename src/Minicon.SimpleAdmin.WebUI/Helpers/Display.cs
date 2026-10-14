using System.Globalization;
using Minicon.SimpleAdmin.Models.Status;
using CheckStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;
using ProblemSeverity = Minicon.SimpleAdmin.Models.State.ProblemSeverity;

namespace Minicon.SimpleAdmin.WebUI.Helpers;

/// <summary>
/// Shared display formatting for the views: local times, durations and German status labels.
/// Every status/severity enum maps to a German label and a tone (ok/warn/crit/ack/muted)
/// which the theme turns into the matching pill colour (<c>pill pill-{tone}</c>).
/// </summary>
internal static class Display
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Status timestamps are UTC; unspecified kinds come from JSON without offset and are UTC as well.</summary>
    public static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    /// <summary>ISO-8601 UTC for <c>data-utc</c> attributes (rendered relative in the browser).</summary>
    public static string Iso(DateTime value) => AsUtc(value).ToString("o", CultureInfo.InvariantCulture);

    public static string LocalTime(DateTime value) => AsUtc(value).ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);

    public static string LocalTimeSeconds(DateTime value) => AsUtc(value).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", German);

    public static string LocalShort(DateTime value) => AsUtc(value).ToLocalTime().ToString("dd.MM. HH:mm", German);

    /// <summary>Compact duration incl. days, e.g. "2 T 3 h", "3 h 12 min", "12 min", "&lt; 1 min".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} T {span.Hours} h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return "< 1 min";
    }

    public static string Label(ServiceStatus status) => status switch
    {
        ServiceStatus.Healthy => "Gesund",
        ServiceStatus.Degraded => "Beeinträchtigt",
        ServiceStatus.Unhealthy => "Kritisch",
        ServiceStatus.Unreachable => "Nicht erreichbar",
        ServiceStatus.Acknowledged => "Quittiert",
        _ => "Unbekannt"
    };

    public static string Tone(ServiceStatus status) => status switch
    {
        ServiceStatus.Healthy => "ok",
        ServiceStatus.Degraded => "warn",
        ServiceStatus.Unhealthy or ServiceStatus.Unreachable => "crit",
        ServiceStatus.Acknowledged => "ack",
        _ => "muted"
    };

    public static string Icon(ServiceStatus status) => status switch
    {
        ServiceStatus.Healthy => "bi-check-circle-fill",
        ServiceStatus.Degraded => "bi-exclamation-triangle-fill",
        ServiceStatus.Unhealthy => "bi-x-octagon-fill",
        ServiceStatus.Unreachable => "bi-wifi-off",
        ServiceStatus.Acknowledged => "bi-check2-circle",
        _ => "bi-question-circle-fill"
    };

    public static string Label(MetricStatus status) => status switch
    {
        MetricStatus.Ok => "OK",
        MetricStatus.Warning => "Warnung",
        MetricStatus.Critical => "Kritisch",
        _ => "Unbekannt"
    };

    public static string Tone(MetricStatus status) => status switch
    {
        MetricStatus.Ok => "ok",
        MetricStatus.Warning => "warn",
        MetricStatus.Critical => "crit",
        _ => "muted"
    };

    /// <summary>Status of the optional checkers (SQL, e-mail, certificates, event log).</summary>
    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Healthy => "OK",
        CheckStatus.Warning => "Warnung",
        CheckStatus.Critical => "Kritisch",
        _ => "Unbekannt"
    };

    public static string Tone(CheckStatus status) => status switch
    {
        CheckStatus.Healthy => "ok",
        CheckStatus.Warning => "warn",
        CheckStatus.Critical => "crit",
        _ => "muted"
    };

    public static string Label(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Critical => "Kritisch",
        ProblemSeverity.Warning => "Warnung",
        _ => "Info"
    };

    public static string Tone(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Critical => "crit",
        ProblemSeverity.Warning => "warn",
        _ => "info"
    };

    public static string Icon(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Critical => "bi-x-octagon-fill",
        ProblemSeverity.Warning => "bi-exclamation-triangle-fill",
        _ => "bi-info-circle-fill"
    };
}
