using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// Test endpoint for the central notifier: resolves the configured SMTP source
/// (an e-mail probe referenced via "ServerId:ProbeName") exactly like the
/// NotificationService does, and sends a test notification to the configured
/// recipients. Lets the user verify the notifier mail path without waiting for
/// a real problem.
/// </summary>
[ApiController]
[Route("api/notificationtest")]
public sealed class NotificationTestController(ConfigurationService configService) : ControllerBase
{
    private const int TimeoutSeconds = 15;

    /// <summary>
    /// Current (possibly unsaved) form values. Null values fall back to the saved config,
    /// so the test always reflects what the user sees in the form.
    /// </summary>
    public record NotificationTestRequest(string? SmtpRef, string? Recipients, string? SubjectPrefix);

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] NotificationTestRequest request)
    {
        var saved = configService.GetFeatures().Notifications ?? new NotificationsFeatureConfig();
        var smtpRef = request.SmtpRef ?? saved.SmtpRef;
        var recipientsRaw = request.Recipients ?? saved.Recipients;
        var subjectPrefix = string.IsNullOrWhiteSpace(request.SubjectPrefix ?? saved.SubjectPrefix)
            ? "[SimpleAdmin]"
            : (request.SubjectPrefix ?? saved.SubjectPrefix);

        // Resolve the SMTP source the same way the notifier does (ServerId:ProbeName,
        // empty = first available probe across all servers).
        var (smtp, resolvedRef) = ResolveSmtp(smtpRef);
        if (smtp == null)
        {
            return Ok(new
            {
                success = false,
                error = string.IsNullOrWhiteSpace(smtpRef)
                    ? "Keine E-Mail-Probe gefunden. Es muss auf mindestens einem Server unter Checks → E-Mail-Proben eine Probe konfiguriert sein — deren SMTP-Zugang wird für den Versand wiederverwendet."
                    : $"SMTP-Quelle '{smtpRef}' nicht gefunden. Erwartetes Format: Server:Probe (z.B. server01:seg1-relay) — Server- und Probe-Name müssen existieren."
            });
        }

        // Recipients: explicit list first, fall back to the probe's monitoring mailbox.
        var recipients = ParseRecipients(recipientsRaw);
        if (recipients.Count == 0)
            recipients = ParseRecipients(smtp.To);
        if (recipients.Count == 0)
        {
            return Ok(new
            {
                success = false,
                error = $"Keine Empfänger: weder im Feld 'Empfänger' noch als 'To' in der aufgelösten Probe ({resolvedRef}) ist eine Adresse hinterlegt."
            });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            var username = configService.DecryptConnectionString(smtp.Username ?? "");
            var password = configService.DecryptConnectionString(smtp.Password ?? "");

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(smtp.From));
            foreach (var recipient in recipients)
                message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = $"{subjectPrefix} Test-Benachrichtigung";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                        Dies ist eine Test-Benachrichtigung des SimpleAdmin-Notifiers.

                        SMTP-Quelle:  {resolvedRef}
                        Server:       {smtp.Host}:{smtp.Port}
                        Absender:     {smtp.From}
                        Empfänger:    {string.Join(", ", recipients)}

                        Wenn diese Mail ankommt, ist der Versandweg für echte
                        Problem-Benachrichtigungen korrekt konfiguriert.
                        """
            };

            using var client = new SmtpClient();
            var sslOptions = ResolveSecureSocketOptions(smtp.SslMode, smtp.UseSsl);
            await client.ConnectAsync(smtp.Host, smtp.Port, sslOptions, cts.Token);
            if (!string.IsNullOrWhiteSpace(username))
                await client.AuthenticateAsync(username, password, cts.Token);
            await client.SendAsync(message, cts.Token);
            await client.DisconnectAsync(quit: true, cts.Token);

            return Ok(new
            {
                success = true,
                smtpSource = resolvedRef,
                host = $"{smtp.Host}:{smtp.Port}",
                from = smtp.From,
                recipients = string.Join(", ", recipients)
            });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { success = false, error = $"Timeout nach {TimeoutSeconds} Sekunden ({smtp.Host}:{smtp.Port})." });
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Ok(new { success = false, error = $"Authentifizierung fehlgeschlagen ({resolvedRef}) — Benutzername oder Passwort der Probe prüfen." });
        }
        catch (SslHandshakeException)
        {
            return Ok(new { success = false, error = "SSL/TLS-Fehler — Zertifikat ungültig oder falscher Port/Protokoll." });
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Ok(new { success = false, error = $"Verbindung fehlgeschlagen — {smtp.Host}:{smtp.Port} nicht erreichbar." });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Mirrors NotificationService.ResolveSmtp: "ServerId:ProbeName" with fallback to the
    /// first available probe across all servers. Additionally returns the resolved
    /// "server:probe" reference for display.
    /// </summary>
    private (SmtpConfig? Smtp, string ResolvedRef) ResolveSmtp(string? smtpRef)
    {
        string? wantServer = null, wantProbe = null;
        if (!string.IsNullOrWhiteSpace(smtpRef))
        {
            var parts = smtpRef.Split(':', 2);
            wantServer = parts[0].Trim();
            if (parts.Length == 2) wantProbe = parts[1].Trim();
        }

        foreach (var server in configService.GetServers())
        {
            if (wantServer != null && !string.Equals(server.Name, wantServer, StringComparison.OrdinalIgnoreCase))
                continue;

            var probes = server.Checks?.EmailProbes?.Probes;
            if (probes == null || probes.Count == 0) continue;

            var probe = wantProbe != null
                ? probes.FirstOrDefault(p => string.Equals(p.Name, wantProbe, StringComparison.OrdinalIgnoreCase))
                : probes[0];

            if (probe != null)
                return (probe.Smtp, $"{server.Name}:{probe.Name}");
        }

        return (null, smtpRef ?? "");
    }

    private static List<string> ParseRecipients(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('@'))
            .ToList();
    }

    private static SecureSocketOptions ResolveSecureSocketOptions(string? sslMode, bool useSsl)
    {
        if (!string.IsNullOrWhiteSpace(sslMode))
        {
            return sslMode.Trim().ToLowerInvariant() switch
            {
                "true" => SecureSocketOptions.SslOnConnect,
                "false" => SecureSocketOptions.None,
                "auto" => SecureSocketOptions.Auto,
                _ => SecureSocketOptions.Auto
            };
        }

        return useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
    }
}
