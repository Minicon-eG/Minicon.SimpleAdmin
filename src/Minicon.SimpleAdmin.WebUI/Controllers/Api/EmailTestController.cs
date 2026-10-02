using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

[ApiController]
[Route("api/emailtest")]
public sealed class EmailTestController(ConfigurationService configService) : ControllerBase
{
    private const int TimeoutSeconds = 10;

    public record SmtpTestRequest(
        string Host,
        int Port,
        bool UseSsl,
        string? SslMode,
        string? Username,
        string? Password,
        string From,
        string To);

    public record ImapTestRequest(
        string Host,
        int Port,
        bool UseSsl,
        string? SslMode,
        string Username,
        string Password,
        string Folder);

    /// <summary>
    /// Tests an SMTP connection by connecting and optionally sending a test email
    /// </summary>
    [HttpPost("smtp")]
    public async Task<IActionResult> TestSmtp([FromBody] SmtpTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
            return Ok(new { success = false, error = "SMTP Host darf nicht leer sein." });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            var password = configService.DecryptConnectionString(request.Password ?? "");
            using var client = new SmtpClient();
            var sslOptions = ResolveSecureSocketOptions(request.SslMode, request.UseSsl);
            await client.ConnectAsync(request.Host, request.Port, sslOptions, cts.Token);

            var serverCapabilities = client.Capabilities.ToString();

            if (!string.IsNullOrWhiteSpace(request.Username))
                await client.AuthenticateAsync(request.Username, password, cts.Token);

            if (!string.IsNullOrWhiteSpace(request.From) && !string.IsNullOrWhiteSpace(request.To))
            {
                var message = new MimeMessage();
                message.From.Add(MailboxAddress.Parse(request.From));
                message.To.Add(MailboxAddress.Parse(request.To));
                message.Subject = "[SimpleAdmin] SMTP-Test";
                message.Body = new TextPart("plain") { Text = "SimpleAdmin SMTP-Verbindungstest." };
                await client.SendAsync(message, cts.Token);
            }

            await client.DisconnectAsync(quit: true, cts.Token);
            return Ok(new { success = true, serverCapabilities });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { success = false, error = $"Timeout nach {TimeoutSeconds} Sekunden." });
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Ok(new { success = false, error = "Authentifizierung fehlgeschlagen — Benutzername oder Passwort prüfen." });
        }
        catch (MailKit.Security.SslHandshakeException)
        {
            return Ok(new { success = false, error = "SSL/TLS-Fehler — Zertifikat ungültig oder falscher Port/Protokoll." });
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Ok(new { success = false, error = "Verbindung fehlgeschlagen — Host nicht erreichbar oder Port geschlossen." });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Tests an IMAP connection by connecting and listing the configured folder
    /// </summary>
    [HttpPost("imap")]
    public async Task<IActionResult> TestImap([FromBody] ImapTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
            return Ok(new { success = false, error = "IMAP Host darf nicht leer sein." });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            var password = configService.DecryptConnectionString(request.Password ?? "");
            using var client = new ImapClient();
            var sslOptions = ResolveSecureSocketOptions(request.SslMode, request.UseSsl);
            await client.ConnectAsync(request.Host, request.Port, sslOptions, cts.Token);
            await client.AuthenticateAsync(request.Username, password, cts.Token);

            var folderName = string.IsNullOrWhiteSpace(request.Folder) ? "INBOX" : request.Folder;
            var folder = await client.GetFolderAsync(folderName, cts.Token);
            await folder.OpenAsync(MailKit.FolderAccess.ReadWrite, cts.Token);

            var messageCount = folder.Count;
            var newestMessageDate = (DateTime?)null;
            if (messageCount > 0)
            {
                var fetchRequest = new MailKit.FetchRequest(MailKit.MessageSummaryItems.Envelope);
                var items = await folder.FetchAsync(messageCount - 1, messageCount - 1, fetchRequest, cts.Token);
                newestMessageDate = items.FirstOrDefault()?.Envelope?.Date?.UtcDateTime;
            }

            await folder.CloseAsync(expunge: false, cts.Token);
            await client.DisconnectAsync(quit: true, cts.Token);

            return Ok(new { success = true, messageCount, newestMessageDate });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { success = false, error = $"Timeout nach {TimeoutSeconds} Sekunden." });
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Ok(new { success = false, error = "Authentifizierung fehlgeschlagen — Benutzername oder Passwort prüfen." });
        }
        catch (MailKit.Security.SslHandshakeException)
        {
            return Ok(new { success = false, error = "SSL/TLS-Fehler — Zertifikat ungültig oder falscher Port/Protokoll." });
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Ok(new { success = false, error = "Verbindung fehlgeschlagen — Host nicht erreichbar oder Port geschlossen." });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
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
                _ => throw new InvalidOperationException($"Unsupported sslMode '{sslMode}'. Allowed: true, false, auto")
            };
        }

        return useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
    }
}
