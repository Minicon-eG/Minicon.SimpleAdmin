using System.Net;
using System.Text;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Renders the central notifier summary mail in two formats from the same data: a plain-text body
/// (always sent as the <c>multipart/alternative</c> fallback) and a rich, inline-styled HTML body
/// (the SimpleAdmin template). Markdown is intentionally avoided — mail clients do not render it.
///
/// Both bodies list every open, unacknowledged problem grouped by server and severity (with
/// remediation steps, wiki links and an acknowledge deep-link), plus a "Behoben / Quittiert" section
/// that states the exact problem that was cleared (taken from the persisted <see cref="NotifyEntry"/>),
/// not just that "a problem" was resolved.
/// </summary>
internal static class NotificationMailRenderer
{
    // Brand / severity palette (inline styles only — email clients strip &lt;style&gt; blocks).
    private const string ColorPageBg = "#eef1f6";
    private const string ColorCardBg = "#ffffff";
    private const string ColorBorder = "#e5e7eb";
    private const string ColorText = "#111827";
    private const string ColorMuted = "#6b7280";
    private const string ColorHeaderBg = "#0f172a";
    private const string ColorAccent = "#2563eb";

    private const string CritColor = "#dc2626";
    private const string CritBg = "#fef2f2";
    private const string CritBorder = "#fecaca";
    private const string WarnColor = "#b45309";
    private const string WarnBg = "#fffbeb";
    private const string WarnBorder = "#fde68a";
    private const string OkColor = "#15803d";
    private const string OkBg = "#f0fdf4";
    private const string OkBorder = "#bbf7d0";

    public readonly record struct RenderedMail(string PlainText, string Html);

    public static RenderedMail Render(
        NotificationsFeatureConfig notif,
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> due,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        DateTime now,
        string? webUiBaseUrl)
    {
        var localNow = now.ToLocalTime();
        var serversAffected = due.Select(d => d.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var critical = due.Count(d => d.Problem.Severity == ProblemSeverity.Critical);
        var warning = due.Count(d => d.Problem.Severity == ProblemSeverity.Warning);
        var reNotifyHours = Math.Max(1, notif.ReNotifyIntervalHours);

        return new RenderedMail(
            BuildPlainText(due, resolved, localNow, webUiBaseUrl, serversAffected, critical, warning, reNotifyHours),
            BuildHtml(due, resolved, localNow, webUiBaseUrl, serversAffected, critical, warning, reNotifyHours));
    }

    // --- plain text ---------------------------------------------------------

    private static string BuildPlainText(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> due,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        DateTime localNow,
        string? webUiBaseUrl,
        int serversAffected,
        int critical,
        int warning,
        int reNotifyHours)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SimpleAdmin Monitoring — {localNow:dd.MM.yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));
        sb.AppendLine();

        if (due.Count > 0)
            sb.AppendLine($"Betroffen: {serversAffected} Server · {critical} kritisch · {warning} Warnung");
        else
            sb.AppendLine($"Entwarnung: {resolved.Count} Problem(e) wieder behoben.");
        sb.AppendLine();

        foreach (var group in due.GroupBy(d => d.ServerId, StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"== {group.Key} ==");
            AppendPlainSeverityBlock(sb, webUiBaseUrl, group.Key, group, ProblemSeverity.Critical, "KRITISCH");
            AppendPlainSeverityBlock(sb, webUiBaseUrl, group.Key, group, ProblemSeverity.Warning, "WARNUNG");
            sb.AppendLine();
        }

        if (resolved.Count > 0)
        {
            sb.AppendLine("== Behoben / Quittiert ==");
            foreach (var (serverId, entry, _) in resolved)
            {
                var detail = string.IsNullOrWhiteSpace(entry.Message)
                    ? (string.IsNullOrEmpty(entry.Severity) ? "Problem" : entry.Severity)
                    : entry.Message;
                sb.AppendLine($"  + {serverId}: {detail}");
                if (entry.FirstSeenAt != default)
                    sb.AppendLine($"      (war seit {entry.FirstSeenAt.ToLocalTime():dd.MM.yyyy HH:mm} aktiv)");
            }
            sb.AppendLine();
        }

        if (due.Count > 0)
            sb.AppendLine($"Erneute Erinnerung für weiterhin offene, nicht quittierte Probleme alle {reNotifyHours} Stunden.");

        sb.AppendLine();
        sb.AppendLine("Diese Nachricht wurde automatisch von SimpleAdmin erzeugt.");
        return sb.ToString();
    }

    private static void AppendPlainSeverityBlock(
        StringBuilder sb,
        string? webUiBaseUrl,
        string serverId,
        IEnumerable<(string ServerId, ActiveProblem Problem)> group,
        ProblemSeverity severity,
        string heading)
    {
        var items = group.Where(g => g.Problem.Severity == severity).ToList();
        if (items.Count == 0)
            return;

        sb.AppendLine($"  {heading}");
        // Collapse identical visible lines (e.g. two service checks sharing a displayName).
        var printed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, problem) in items)
        {
            if (!printed.Add(problem.Message))
                continue;
            sb.AppendLine($"   - {problem.Message}");
            if (!string.IsNullOrEmpty(problem.WikiUrl))
                sb.AppendLine($"     Anleitung: {problem.WikiUrl}");
            foreach (var step in problem.RemediationSteps)
                sb.AppendLine($"       • {step}");
            var link = BuildAckLink(webUiBaseUrl, serverId, problem.Id);
            if (link != null)
                sb.AppendLine($"     Quittieren: {link}");
        }
    }

    // --- HTML ---------------------------------------------------------------

    private static string BuildHtml(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> due,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        DateTime localNow,
        string? webUiBaseUrl,
        int serversAffected,
        int critical,
        int warning,
        int reNotifyHours)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<meta name=\"color-scheme\" content=\"light\"></head>");
        sb.Append($"<body style=\"margin:0;padding:0;background:{ColorPageBg};\">");
        sb.Append($"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">{Enc(BuildPreheader(due, resolved, serversAffected, critical, warning))}</div>");

        // Outer centering table.
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:{ColorPageBg};\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
        sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:600px;max-width:600px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;\">");

        AppendHtmlHeader(sb, localNow);
        AppendHtmlSummary(sb, due, resolved, serversAffected, critical, warning);

        sb.Append($"<tr><td style=\"background:{ColorCardBg};padding:8px 24px 24px 24px;\">");

        foreach (var group in due.GroupBy(d => d.ServerId, StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendHtmlServerCard(sb, webUiBaseUrl, group.Key, group);
        }

        if (resolved.Count > 0)
            AppendHtmlResolved(sb, resolved);

        sb.Append("</td></tr>");

        AppendHtmlFooter(sb, due.Count > 0 ? reNotifyHours : (int?)null);

        sb.Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    private static string BuildPreheader(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> due,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        int serversAffected,
        int critical,
        int warning)
        => due.Count > 0
            ? $"{serversAffected} Server betroffen — {critical} kritisch, {warning} Warnung"
            : $"Entwarnung — {resolved.Count} Problem(e) wieder behoben";

    private static void AppendHtmlHeader(StringBuilder sb, DateTime localNow)
    {
        sb.Append("<tr><td style=\"border-radius:12px 12px 0 0;overflow:hidden;\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{ColorHeaderBg}\" " +
                  $"style=\"background:{ColorHeaderBg};background:linear-gradient(135deg,#1e3a8a 0%,{ColorHeaderBg} 70%);\">");
        sb.Append("<tr><td style=\"padding:24px 24px 22px 24px;\">");
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        sb.Append("<td style=\"vertical-align:middle;\">");
        sb.Append($"<span style=\"display:inline-block;width:38px;height:38px;border-radius:9px;background:{ColorAccent};" +
                  "color:#ffffff;font-size:20px;line-height:38px;text-align:center;font-weight:700;\">SA</span>");
        sb.Append("</td><td style=\"vertical-align:middle;padding-left:12px;\">");
        sb.Append("<div style=\"color:#ffffff;font-size:18px;font-weight:700;line-height:1.2;\">SimpleAdmin Monitoring</div>");
        sb.Append($"<div style=\"color:#cbd5e1;font-size:13px;line-height:1.2;padding-top:2px;\">Statusbericht · {Enc(localNow.ToString("dd.MM.yyyy HH:mm"))}</div>");
        sb.Append("</td></tr></table>");
        sb.Append("</td></tr></table></td></tr>");
    }

    private static void AppendHtmlSummary(
        StringBuilder sb,
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> due,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved,
        int serversAffected,
        int critical,
        int warning)
    {
        sb.Append($"<tr><td style=\"background:{ColorCardBg};padding:20px 24px 4px 24px;\">");

        if (due.Count == 0)
        {
            sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{OkBg}\" " +
                      $"style=\"background:{OkBg};border:1px solid {OkBorder};border-radius:10px;\"><tr>" +
                      $"<td style=\"padding:16px 18px;color:{OkColor};font-size:15px;font-weight:600;\">" +
                      $"&#10003; Entwarnung — {resolved.Count} Problem(e) wieder behoben</td></tr></table>");
            sb.Append("</td></tr>");
            return;
        }

        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
        AppendStatCell(sb, serversAffected.ToString(), "Server betroffen", ColorText, ColorBorder, ColorPageBg, isFirst: true);
        AppendStatCell(sb, critical.ToString(), "Kritisch", CritColor, CritBorder, CritBg, isFirst: false);
        AppendStatCell(sb, warning.ToString(), "Warnungen", WarnColor, WarnBorder, WarnBg, isFirst: false);
        sb.Append("</tr></table>");
        sb.Append("</td></tr>");
    }

    private static void AppendStatCell(StringBuilder sb, string value, string label, string valueColor, string border, string bg, bool isFirst)
    {
        var pad = isFirst ? "0" : "0 0 0 10px";
        sb.Append($"<td width=\"33%\" style=\"padding:{pad};\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{bg}\" " +
                  $"style=\"background:{bg};border:1px solid {border};border-radius:10px;\"><tr>" +
                  $"<td align=\"center\" style=\"padding:14px 6px;\">" +
                  $"<div style=\"color:{valueColor};font-size:26px;font-weight:700;line-height:1;\">{Enc(value)}</div>" +
                  $"<div style=\"color:{ColorMuted};font-size:11px;text-transform:uppercase;letter-spacing:.5px;padding-top:5px;\">{Enc(label)}</div>" +
                  $"</td></tr></table></td>");
    }

    private static void AppendHtmlServerCard(
        StringBuilder sb,
        string? webUiBaseUrl,
        string serverId,
        IEnumerable<(string ServerId, ActiveProblem Problem)> group)
    {
        var problems = group.ToList();
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                  $"style=\"margin-top:18px;border:1px solid {ColorBorder};border-radius:10px;\">");
        // Card header: server name.
        sb.Append("<tr><td style=\"padding:13px 16px;border-bottom:1px solid " + ColorBorder + ";\">" +
                  $"<span style=\"color:{ColorMuted};font-size:11px;text-transform:uppercase;letter-spacing:.5px;\">Server</span><br>" +
                  $"<span style=\"color:{ColorText};font-size:16px;font-weight:700;\">{Enc(serverId)}</span></td></tr>");
        sb.Append("<tr><td style=\"padding:6px 16px 14px 16px;\">");

        AppendHtmlProblemRows(sb, webUiBaseUrl, serverId, problems, ProblemSeverity.Critical);
        AppendHtmlProblemRows(sb, webUiBaseUrl, serverId, problems, ProblemSeverity.Warning);

        sb.Append("</td></tr></table>");
    }

    private static void AppendHtmlProblemRows(
        StringBuilder sb,
        string? webUiBaseUrl,
        string serverId,
        IEnumerable<(string ServerId, ActiveProblem Problem)> problems,
        ProblemSeverity severity)
    {
        var items = problems.Where(p => p.Problem.Severity == severity).ToList();
        if (items.Count == 0)
            return;

        var (accent, bg, border, badge) = severity == ProblemSeverity.Critical
            ? (CritColor, CritBg, CritBorder, "KRITISCH")
            : (WarnColor, WarnBg, WarnBorder, "WARNUNG");

        var printed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, problem) in items)
        {
            if (!printed.Add(problem.Message))
                continue;

            sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{bg}\" " +
                      $"style=\"margin-top:10px;background:{bg};border:1px solid {border};border-left:4px solid {accent};border-radius:8px;\">");
            sb.Append("<tr><td style=\"padding:12px 14px;\">");

            sb.Append($"<span style=\"display:inline-block;background:{accent};color:#ffffff;font-size:10px;font-weight:700;" +
                      $"letter-spacing:.5px;padding:2px 8px;border-radius:4px;\">{badge}</span>");
            sb.Append($"<div style=\"color:{ColorText};font-size:14px;font-weight:600;line-height:1.45;padding-top:8px;\">{Enc(problem.Message)}</div>");

            if (problem.RemediationSteps.Count > 0)
            {
                sb.Append($"<div style=\"color:{ColorMuted};font-size:12px;font-weight:600;padding-top:8px;\">Maßnahmen</div>");
                sb.Append($"<ul style=\"margin:4px 0 0 0;padding-left:18px;color:{ColorText};font-size:13px;line-height:1.5;\">");
                foreach (var step in problem.RemediationSteps)
                    sb.Append($"<li>{Enc(step)}</li>");
                sb.Append("</ul>");
            }

            // Action row: acknowledge button + optional wiki link.
            var ackLink = BuildAckLink(webUiBaseUrl, serverId, problem.Id);
            if (ackLink != null || !string.IsNullOrEmpty(problem.WikiUrl))
            {
                sb.Append("<div style=\"padding-top:12px;\">");
                if (ackLink != null)
                    sb.Append($"<a href=\"{Enc(ackLink)}\" style=\"display:inline-block;background:{ColorAccent};color:#ffffff;" +
                              "font-size:13px;font-weight:600;text-decoration:none;padding:8px 16px;border-radius:6px;\">Quittieren</a>");
                if (!string.IsNullOrEmpty(problem.WikiUrl))
                    sb.Append($"<a href=\"{Enc(problem.WikiUrl)}\" style=\"display:inline-block;color:{ColorAccent};font-size:13px;" +
                              "font-weight:600;text-decoration:none;padding:8px 12px;\">Anleitung &#8599;</a>");
                sb.Append("</div>");
            }

            sb.Append("</td></tr></table>");
        }
    }

    private static void AppendHtmlResolved(
        StringBuilder sb,
        IReadOnlyList<(string ServerId, NotifyEntry Entry, string ProblemId)> resolved)
    {
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                  $"style=\"margin-top:18px;border:1px solid {OkBorder};border-radius:10px;\">");
        sb.Append($"<tr><td style=\"padding:13px 16px;border-bottom:1px solid {OkBorder};background:{OkBg};border-radius:10px 10px 0 0;\">" +
                  $"<span style=\"color:{OkColor};font-size:14px;font-weight:700;\">&#10003; Behoben / Quittiert</span></td></tr>");
        sb.Append("<tr><td style=\"padding:6px 16px 14px 16px;\">");

        foreach (var (serverId, entry, _) in resolved)
        {
            var detail = string.IsNullOrWhiteSpace(entry.Message)
                ? (string.IsNullOrEmpty(entry.Severity) ? "Problem" : entry.Severity)
                : entry.Message;

            sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{OkBg}\" " +
                      $"style=\"margin-top:10px;background:{OkBg};border:1px solid {OkBorder};border-left:4px solid {OkColor};border-radius:8px;\">");
            sb.Append("<tr><td style=\"padding:11px 14px;\">");
            sb.Append($"<div style=\"color:{ColorMuted};font-size:11px;text-transform:uppercase;letter-spacing:.5px;\">{Enc(serverId)}</div>");
            sb.Append($"<div style=\"color:{ColorText};font-size:14px;font-weight:600;line-height:1.45;padding-top:3px;\">{Enc(detail)}</div>");
            if (entry.FirstSeenAt != default)
                sb.Append($"<div style=\"color:{ColorMuted};font-size:12px;padding-top:4px;\">war seit {Enc(entry.FirstSeenAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))} aktiv</div>");
            sb.Append("</td></tr></table>");
        }

        sb.Append("</td></tr></table>");
    }

    private static void AppendHtmlFooter(StringBuilder sb, int? reNotifyHours)
    {
        sb.Append($"<tr><td style=\"background:{ColorCardBg};border-radius:0 0 12px 12px;padding:4px 24px 22px 24px;\">");
        if (reNotifyHours.HasValue)
            sb.Append($"<div style=\"color:{ColorMuted};font-size:12px;line-height:1.5;border-top:1px solid {ColorBorder};padding-top:14px;\">" +
                      $"Erneute Erinnerung für weiterhin offene, nicht quittierte Probleme alle {reNotifyHours.Value} Stunden.</div>");
        sb.Append($"<div style=\"color:#9ca3af;font-size:11px;line-height:1.5;padding-top:10px;\">" +
                  "Diese Nachricht wurde automatisch von SimpleAdmin erzeugt.</div>");
        sb.Append("</td></tr>");
    }

    // --- scheduled status report -------------------------------------------

    /// <summary>
    /// Renders the scheduled status report: every server with open (unacknowledged) warnings or
    /// errors with its problems, the acknowledged problems as a short list, and the servers without
    /// findings — or a prominent "Alles OK" block when nothing is open.
    /// </summary>
    public static RenderedMail RenderStatusReport(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> open,
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> acknowledged,
        IReadOnlyList<string> servers,
        DateTime now,
        string? webUiBaseUrl)
    {
        var localNow = now.ToLocalTime();
        var affected = open.Select(o => o.ServerId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var okServers = servers
            .Where(s => !affected.Contains(s, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RenderedMail(
            BuildReportPlainText(open, acknowledged, okServers, servers.Count, affected.Count, localNow, webUiBaseUrl),
            BuildReportHtml(open, acknowledged, okServers, servers.Count, affected.Count, localNow, webUiBaseUrl));
    }

    private static string BuildReportPlainText(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> open,
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> acknowledged,
        IReadOnlyList<string> okServers,
        int serverCount,
        int affectedCount,
        DateTime localNow,
        string? webUiBaseUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SimpleAdmin Statusbericht — {localNow:dd.MM.yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));
        sb.AppendLine();

        if (open.Count == 0)
        {
            sb.AppendLine($"ALLES OK — alle {serverCount} Server ohne offene Warnung oder Fehler.");
        }
        else
        {
            var critical = open.Count(o => o.Problem.Severity == ProblemSeverity.Critical);
            var warning = open.Count(o => o.Problem.Severity == ProblemSeverity.Warning);
            sb.AppendLine($"{affectedCount} von {serverCount} Servern mit Problemen · {critical} kritisch · {warning} Warnung");
            sb.AppendLine();

            foreach (var group in open.GroupBy(o => o.ServerId, StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"== {group.Key} ==");
                AppendPlainSeverityBlock(sb, webUiBaseUrl, group.Key, group, ProblemSeverity.Critical, "KRITISCH");
                AppendPlainSeverityBlock(sb, webUiBaseUrl, group.Key, group, ProblemSeverity.Warning, "WARNUNG");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        if (acknowledged.Count > 0)
        {
            sb.AppendLine("== Quittiert (bekannt) ==");
            foreach (var (serverId, problem) in acknowledged.OrderBy(a => a.ServerId, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  ~ {serverId}: {problem.Message} ({AckUntil(problem)})");
            sb.AppendLine();
        }

        if (open.Count > 0 && okServers.Count > 0)
        {
            sb.AppendLine($"Ohne Befund ({okServers.Count}): {string.Join(", ", okServers)}");
            sb.AppendLine();
        }

        sb.AppendLine("Diese Nachricht wurde automatisch von SimpleAdmin erzeugt.");
        return sb.ToString();
    }

    private static string BuildReportHtml(
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> open,
        IReadOnlyList<(string ServerId, ActiveProblem Problem)> acknowledged,
        IReadOnlyList<string> okServers,
        int serverCount,
        int affectedCount,
        DateTime localNow,
        string? webUiBaseUrl)
    {
        var critical = open.Count(o => o.Problem.Severity == ProblemSeverity.Critical);
        var warning = open.Count(o => o.Problem.Severity == ProblemSeverity.Warning);
        var preheader = open.Count == 0
            ? $"Alles OK — alle {serverCount} Server ohne offene Warnung oder Fehler"
            : $"{affectedCount} von {serverCount} Servern mit Problemen — {critical} kritisch, {warning} Warnung";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<meta name=\"color-scheme\" content=\"light\"></head>");
        sb.Append($"<body style=\"margin:0;padding:0;background:{ColorPageBg};\">");
        sb.Append($"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">{Enc(preheader)}</div>");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:{ColorPageBg};\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
        sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:600px;max-width:600px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;\">");

        AppendHtmlHeader(sb, localNow);

        // Summary
        sb.Append($"<tr><td style=\"background:{ColorCardBg};padding:20px 24px 4px 24px;\">");
        if (open.Count == 0)
        {
            sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" bgcolor=\"{OkBg}\" " +
                      $"style=\"background:{OkBg};border:1px solid {OkBorder};border-radius:10px;\"><tr>" +
                      $"<td style=\"padding:18px;color:{OkColor};font-size:17px;font-weight:700;\">" +
                      $"&#10003; Alles OK<div style=\"font-size:13px;font-weight:400;padding-top:4px;\">" +
                      $"Alle {serverCount} Server ohne offene Warnung oder Fehler.</div></td></tr></table>");
        }
        else
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            AppendStatCell(sb, $"{affectedCount}/{serverCount}", "Server betroffen", ColorText, ColorBorder, ColorPageBg, isFirst: true);
            AppendStatCell(sb, critical.ToString(), "Kritisch", CritColor, CritBorder, CritBg, isFirst: false);
            AppendStatCell(sb, warning.ToString(), "Warnungen", WarnColor, WarnBorder, WarnBg, isFirst: false);
            sb.Append("</tr></table>");
        }
        sb.Append("</td></tr>");

        sb.Append($"<tr><td style=\"background:{ColorCardBg};padding:8px 24px 24px 24px;\">");

        foreach (var group in open.GroupBy(o => o.ServerId, StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendHtmlServerCard(sb, webUiBaseUrl, group.Key, group);
        }

        if (acknowledged.Count > 0)
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" " +
                      $"style=\"margin-top:18px;border:1px solid {ColorBorder};border-radius:10px;\">");
            sb.Append($"<tr><td style=\"padding:13px 16px;border-bottom:1px solid {ColorBorder};\">" +
                      $"<span style=\"color:{ColorMuted};font-size:14px;font-weight:700;\">Quittiert (bekannt)</span></td></tr>");
            sb.Append($"<tr><td style=\"padding:8px 16px 12px 16px;color:{ColorText};font-size:13px;line-height:1.6;\">");
            foreach (var (serverId, problem) in acknowledged.OrderBy(a => a.ServerId, StringComparer.OrdinalIgnoreCase))
                sb.Append($"<div><span style=\"color:{ColorMuted};\">{Enc(serverId)}:</span> {Enc(problem.Message)} " +
                          $"<span style=\"color:{ColorMuted};\">({Enc(AckUntil(problem))})</span></div>");
            sb.Append("</td></tr></table>");
        }

        if (open.Count > 0 && okServers.Count > 0)
        {
            sb.Append($"<div style=\"margin-top:18px;color:{ColorMuted};font-size:12px;line-height:1.6;\">" +
                      $"<span style=\"color:{OkColor};font-weight:700;\">&#10003; Ohne Befund ({okServers.Count}):</span> " +
                      $"<span class=\"ok-servers\" style=\"font-family:Consolas,monospace;\">{Enc(string.Join(", ", okServers))}</span></div>");
        }

        sb.Append("</td></tr>");
        AppendHtmlFooter(sb, reNotifyHours: null);
        sb.Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    // --- helpers ------------------------------------------------------------

    private static string? BuildAckLink(string? baseUrl, string serverId, string problemId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;
        var root = baseUrl!.TrimEnd('/');
        return $"{root}/Problems?ackServer={Uri.EscapeDataString(serverId)}&ackProblem={Uri.EscapeDataString(problemId)}";
    }

    private static string AckUntil(ActiveProblem problem)
        => problem.AcknowledgeExpiresAt.HasValue
            ? $"quittiert bis {problem.AcknowledgeExpiresAt.Value.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "unbegrenzt quittiert";

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
