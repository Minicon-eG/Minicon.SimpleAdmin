using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// A single plain-text e-mail to send via SMTP.
/// </summary>
public sealed record SmtpMailRequest
{
    /// <summary>SMTP server + sender configuration (host, port, ssl, from, credentials).</summary>
    public required SmtpConfig Smtp { get; init; }

    /// <summary>Recipient addresses.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>Subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>Plain-text body (always sent — the fallback alternative for clients without HTML).</summary>
    public required string Body { get; init; }

    /// <summary>
    /// Optional HTML body. When set, the mail is sent as <c>multipart/alternative</c> (plain text +
    /// HTML) so HTML-capable clients render the rich template while everything else falls back to
    /// <see cref="Body"/>. Null/empty → plain-text only.
    /// </summary>
    public string? HtmlBody { get; init; }

    /// <summary>Master key to decrypt an encrypted SMTP password (Encryption:ConnectionStringKey). Empty = plain-text only.</summary>
    public string MasterKey { get; init; } = string.Empty;

    /// <summary>Short label for diagnostic logging (e.g. "Notifier", "CertNotif").</summary>
    public string ContextLabel { get; init; } = "Mail";
}

/// <summary>
/// Shared SMTP sender. Extracted from the MailKit code in EmailProbeSender so all notification
/// paths (probes, central notifier) use one connect/auth/send routine.
/// </summary>
public interface ISmtpMailSender
{
    Task SendAsync(SmtpMailRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SmtpMailSender(ILogger<SmtpMailSender> logger) : ISmtpMailSender
{
    public async Task SendAsync(SmtpMailRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Recipients.Count == 0)
            throw new InvalidOperationException($"{request.ContextLabel}: no recipients to send to");

        var password = DecryptCredential(request.Smtp.Password, request.MasterKey, request.ContextLabel);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(request.Smtp.From));
        foreach (var recipient in request.Recipients)
            message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = request.Subject;
        if (!string.IsNullOrEmpty(request.HtmlBody))
        {
            // multipart/alternative: ordered text-first so HTML-capable clients pick the rich part.
            message.Body = new BodyBuilder { TextBody = request.Body, HtmlBody = request.HtmlBody }.ToMessageBody();
        }
        else
        {
            message.Body = new TextPart("plain") { Text = request.Body };
        }

        using var client = new SmtpClient();
        client.Timeout = (request.Smtp.TimeoutSeconds ?? 30) * 1_000;
        var sslOptions = ResolveSecureSocketOptions(request.Smtp.SslMode, request.Smtp.UseSsl);

        logger.LogDebug("{Context}: connecting to {Host}:{Port} ssl={Ssl} timeout={TimeoutMs}ms",
            request.ContextLabel, request.Smtp.Host, request.Smtp.Port, sslOptions, client.Timeout);
        await client.ConnectAsync(request.Smtp.Host, request.Smtp.Port, sslOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Smtp.Username))
        {
            logger.LogDebug("{Context}: authenticating as '{Username}'", request.ContextLabel, request.Smtp.Username);
            await client.AuthenticateAsync(request.Smtp.Username, password, cancellationToken);
        }
        else if (client.AuthenticationMechanisms.Contains("NTLM") || client.AuthenticationMechanisms.Contains("GSSAPI"))
        {
            logger.LogDebug("{Context}: no explicit credentials, using DefaultNetworkCredentials", request.ContextLabel);
            await client.AuthenticateAsync(CredentialCache.DefaultNetworkCredentials, cancellationToken);
        }

        logger.LogDebug("{Context}: sending message From={From} To={To} Subject={Subject}",
            request.ContextLabel, request.Smtp.From, string.Join(",", request.Recipients), request.Subject);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        logger.LogDebug("{Context}: message accepted by server", request.ContextLabel);
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
                _ => throw new InvalidOperationException($"Unsupported SMTP sslMode '{sslMode}'. Allowed: true, false, auto")
            };
        }

        return useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;
    }

    private string DecryptCredential(string? value, string masterKey, string contextLabel)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!ConnectionStringEncryption.IsEncrypted(value))
            return value;

        if (string.IsNullOrEmpty(masterKey))
        {
            logger.LogWarning("{Context}: SMTP password is encrypted but Encryption:ConnectionStringKey is not configured", contextLabel);
            throw new InvalidOperationException($"{contextLabel}: encrypted SMTP password but no encryption key configured");
        }

        return ConnectionStringEncryption.Decrypt(value, masterKey);
    }
}
