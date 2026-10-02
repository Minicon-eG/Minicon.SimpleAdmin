using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Sends scheduled probe emails via SMTP.
/// Two state files are maintained per probe:
///   emailprobe.{name}.lastattempt — timestamp of every attempt (used for rate-limiting)
///   emailprobe.{name}.lastsent    — timestamp of the last SUCCESSFUL send (displayed in UI)
/// Using separate files ensures that SMTP failures do not bypass the interval gate
/// (Gap 4: retries were previously triggered on every worker cycle during outages).
/// </summary>
public sealed class EmailProbeSender(
    ILogger<EmailProbeSender> logger,
    IDateTimeProvider dateTime,
    string stateDir) : IEmailProbeSender
{
    public async Task<Dictionary<string, EmailProbeState>> SendPendingProbesAsync(
        List<EmailProbeConfig> probes,
        string masterKey,
        Dictionary<string, EmailProbeState>? previousStates = null,
        bool forceSend = false,
        CancellationToken cancellationToken = default)
    {
        var tasks = probes
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => SendProbeAsync(p, masterKey, previousStates, forceSend, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.ProbeId, r => r);
    }

    private async Task<EmailProbeState> SendProbeAsync(
        EmailProbeConfig probe,
        string masterKey,
        Dictionary<string, EmailProbeState>? previousStates,
        bool forceSend,
        CancellationToken cancellationToken)
    {
        var now = dateTime.UtcNow;
        var prevFailures = previousStates?.GetValueOrDefault(probe.Name)?.ConsecutiveFailures ?? 0;

        var state = new EmailProbeState
        {
            ProbeId = probe.Name,
            ProbeName = string.IsNullOrWhiteSpace(probe.Description) ? probe.Name : probe.Description,
            LastChecked = now
        };

        // Rate-limit using the last-attempt timestamp (written on every attempt, success or failure).
        // This prevents retrying on every worker cycle during an SMTP outage (Gap 4).
        var lastAttemptAt = ReadTimestamp(probe.Name, "lastattempt");
        var elapsed = now - lastAttemptAt;
        if (!forceSend && elapsed.TotalMinutes < probe.SendIntervalMinutes)
        {
            state.Status = MetricStatus.Healthy;
            var lastSentSkip = ReadTimestamp(probe.Name, "lastsent");
            state.LastSentAt = lastSentSkip != DateTime.MinValue ? lastSentSkip : null;
            state.ConsecutiveFailures = prevFailures;  // carry forward — don't reset on skip
            logger.LogDebug("Email probe '{ProbeName}': next attempt in {RemainingMinutes:F0} min",
                probe.Name, probe.SendIntervalMinutes - elapsed.TotalMinutes);
            return state;
        }

        if (forceSend)
        {
            logger.LogInformation("Email probe '{ProbeName}': forceSend=true, bypassing send interval", probe.Name);
        }
        else
        {
            logger.LogDebug("Email probe '{ProbeName}': interval elapsed ({ElapsedMinutes:F1} min >= {IntervalMinutes} min), attempting send",
                probe.Name, elapsed.TotalMinutes, probe.SendIntervalMinutes);
        }

        var password = DecryptCredential(probe.Smtp.Password, masterKey, probe.Name, "SMTP password");
        if (password == null)
        {
            // Mark attempt so we respect the interval even after a decrypt failure
            WriteTimestamp(probe.Name, "lastattempt", now);
            state.Status = MetricStatus.Critical;
            state.ErrorMessage = "Encryption key not configured — cannot decrypt SMTP password";
            state.ConsecutiveFailures = prevFailures + 1;
            return state;
        }

        // Write attempt timestamp BEFORE connecting so the interval is respected even if the
        // process is killed mid-send or the SMTP call hangs until timeout.
        WriteTimestamp(probe.Name, "lastattempt", now);

        try
        {
            await SendEmailAsync(probe, password, now, cancellationToken);

            WriteTimestamp(probe.Name, "lastsent", now);
            state.LastSentAt = now;
            state.Status = MetricStatus.Healthy;
            state.ConsecutiveFailures = 0;
            logger.LogInformation("Email probe '{ProbeName}': sent successfully", probe.Name);
        }
        catch (Exception ex)
        {
            state.Status = MetricStatus.Critical;
            state.ErrorMessage = ex.Message;
            state.ConsecutiveFailures = prevFailures + 1;
            // Preserve the last known good send timestamp for the UI
            var lastSentFail = ReadTimestamp(probe.Name, "lastsent");
            state.LastSentAt = lastSentFail != DateTime.MinValue ? lastSentFail : null;
            logger.LogError(ex, "Email probe '{ProbeName}': SMTP send failed", probe.Name);
        }

        return state;
    }

    private async Task SendEmailAsync(EmailProbeConfig probe, string password, DateTime sentAt, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(probe.Smtp.From));
        message.To.Add(MailboxAddress.Parse(probe.Smtp.To));
        message.Subject = probe.Subject;
        message.Body = new TextPart("plain")
        {
            Text = $"SimpleAdmin Email Probe — {probe.Name} — {sentAt:u}"
        };

        using var client = new SmtpClient();
        client.Timeout = (probe.Smtp.TimeoutSeconds ?? 30) * 1_000;
        var sslOptions = ResolveSecureSocketOptions(probe.Smtp.SslMode, probe.Smtp.UseSsl);

        logger.LogDebug("Email probe '{ProbeName}': connecting to {Host}:{Port} ssl={SslOptions} timeout={TimeoutMs}ms",
            probe.Name, probe.Smtp.Host, probe.Smtp.Port, sslOptions, client.Timeout);
        await client.ConnectAsync(probe.Smtp.Host, probe.Smtp.Port, sslOptions, cancellationToken);
        logger.LogDebug("Email probe '{ProbeName}': connected", probe.Name);

        var authMechanisms = string.Join(", ", client.AuthenticationMechanisms.OrderBy(x => x));
        logger.LogDebug("Email probe '{ProbeName}': server auth mechanisms: {Mechanisms}", probe.Name, authMechanisms);

        if (!string.IsNullOrWhiteSpace(probe.Smtp.Username))
        {
            logger.LogDebug("Email probe '{ProbeName}': authenticating via configured username/password as '{Username}'",
                probe.Name, probe.Smtp.Username);
            await client.AuthenticateAsync(probe.Smtp.Username, password, cancellationToken);
            logger.LogDebug("Email probe '{ProbeName}': authentication successful", probe.Name);
        }
        else if (client.AuthenticationMechanisms.Contains("NTLM") || client.AuthenticationMechanisms.Contains("GSSAPI"))
        {
            logger.LogDebug("Email probe '{ProbeName}': no explicit credentials configured, using DefaultNetworkCredentials",
                probe.Name);
            await client.AuthenticateAsync(CredentialCache.DefaultNetworkCredentials, cancellationToken);
            logger.LogDebug("Email probe '{ProbeName}': integrated authentication successful", probe.Name);
        }
        else
        {
            logger.LogDebug("Email probe '{ProbeName}': no credentials configured and no supported integrated auth mechanism available, skipping auth", probe.Name);
        }

        logger.LogDebug("Email probe '{ProbeName}': sending message From={From} To={To} Subject={Subject}",
            probe.Name, probe.Smtp.From, probe.Smtp.To, probe.Subject);
        await client.SendAsync(message, cancellationToken);
        logger.LogDebug("Email probe '{ProbeName}': message accepted by server", probe.Name);

        logger.LogDebug("Email probe '{ProbeName}': disconnecting", probe.Name);
        await client.DisconnectAsync(quit: true, cancellationToken);
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

    private string? DecryptCredential(string? value, string masterKey, string probeName, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!ConnectionStringEncryption.IsEncrypted(value))
            return value;

        if (string.IsNullOrEmpty(masterKey))
        {
            logger.LogWarning("Email probe '{ProbeName}': {FieldName} is encrypted but Encryption:ConnectionStringKey is not configured",
                probeName, fieldName);
            return null;
        }

        return ConnectionStringEncryption.Decrypt(value, masterKey);
    }

    private DateTime ReadTimestamp(string probeName, string suffix)
    {
        var path = GetStateFilePath(probeName, suffix);
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (DateTime.TryParse(text, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email probe '{ProbeName}': failed to read state file '{Path}'", probeName, path);
        }

        return DateTime.MinValue;
    }

    private void WriteTimestamp(string probeName, string suffix, DateTime timestamp)
    {
        var path = GetStateFilePath(probeName, suffix);
        try
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(path, timestamp.ToString("O"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email probe '{ProbeName}': failed to write state file '{Path}'", probeName, path);
        }
    }

    private string GetStateFilePath(string probeName, string suffix)
    {
        var safe = string.Concat(probeName.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        return Path.Combine(stateDir, $"emailprobe.{safe}.{suffix}");
    }
}
