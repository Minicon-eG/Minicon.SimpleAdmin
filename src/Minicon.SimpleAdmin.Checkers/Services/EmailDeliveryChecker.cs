using System.Net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks that probe emails arrive in the configured IMAP mailbox within the expected time window.
/// Opens one IMAP connection per cycle and reuses it for all subject-filter checks.
/// </summary>
public sealed class EmailDeliveryChecker(
    ILogger<EmailDeliveryChecker> logger,
    IDateTimeProvider dateTime) : IEmailDeliveryChecker
{
    public async Task<Dictionary<string, EmailDeliveryCheckState>> CheckDeliveryAsync(
        ServerEmailDeliveryConfig config,
        string masterKey,
        Dictionary<string, EmailDeliveryCheckState>? previousStates = null,
        CancellationToken cancellationToken = default)
    {
        var now = dateTime.UtcNow;
        var results = new Dictionary<string, EmailDeliveryCheckState>();

        if (config.Checks.Count == 0)
            return results;

        // Warn about overlapping subject filters — one check can inadvertently consume another's messages (Gap 5).
        for (int i = 0; i < config.Checks.Count; i++)
        {
            for (int j = i + 1; j < config.Checks.Count; j++)
            {
                var filterA = config.Checks[i].SubjectFilter;
                var filterB = config.Checks[j].SubjectFilter;
                if (filterA.Contains(filterB, StringComparison.OrdinalIgnoreCase) ||
                    filterB.Contains(filterA, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Email delivery checks '{CheckA}' and '{CheckB}' have overlapping subject filters " +
                        "('{FilterA}' / '{FilterB}'). With deleteAfterCheck=true one check may consume the " +
                        "other's messages. Ensure subject filters are non-overlapping.",
                        config.Checks[i].Name, config.Checks[j].Name, filterA, filterB);
                }
            }
        }

        var password = DecryptCredential(config.Imap.Password, masterKey);
        if (password == null)
        {
            // Encryption key missing — mark all checks Critical immediately
            foreach (var check in config.Checks)
            {
                var prevFailures = previousStates?.GetValueOrDefault(check.Name)?.ConsecutiveFailures ?? 0;
                results[check.Name] = new EmailDeliveryCheckState
                {
                    CheckId = check.Name,
                    CheckName = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description,
                    LastChecked = now,
                    Status = MetricStatus.Critical,
                    ConsecutiveFailures = prevFailures + 1,
                    ErrorMessage = "Encryption key not configured — cannot decrypt IMAP password"
                };
            }
            return results;
        }

        using var client = new ImapClient();
        client.Timeout = (config.Imap.TimeoutSeconds ?? 30) * 1_000;
        try
        {
            var sslOptions = ResolveSecureSocketOptions(config.Imap.SslMode, config.Imap.UseSsl);

            logger.LogDebug("Email delivery checker: connecting to {Host}:{Port} ssl={SslOptions} timeout={TimeoutMs}ms",
                config.Imap.Host, config.Imap.Port, sslOptions, client.Timeout);
            await client.ConnectAsync(config.Imap.Host, config.Imap.Port, sslOptions, cancellationToken);
            logger.LogDebug("Email delivery checker: connected");

            var authMechanisms = string.Join(", ", client.AuthenticationMechanisms.OrderBy(x => x));
            logger.LogDebug("Email delivery checker: server auth mechanisms: {Mechanisms}", authMechanisms);

            if (!string.IsNullOrWhiteSpace(config.Imap.Username))
            {
                logger.LogDebug("Email delivery checker: authenticating via configured username/password as '{Username}'", config.Imap.Username);
                await client.AuthenticateAsync(config.Imap.Username, password, cancellationToken);
                logger.LogDebug("Email delivery checker: authentication successful");
            }
            else if (client.AuthenticationMechanisms.Contains("NTLM") || client.AuthenticationMechanisms.Contains("GSSAPI"))
            {
                logger.LogDebug("Email delivery checker: no explicit credentials configured, using DefaultNetworkCredentials");
                await client.AuthenticateAsync(CredentialCache.DefaultNetworkCredentials, cancellationToken);
                logger.LogDebug("Email delivery checker: integrated authentication successful");
            }
            else
            {
                logger.LogDebug("Email delivery checker: no credentials configured and no supported integrated auth mechanism available, skipping auth");
            }

            logger.LogDebug("Email delivery checker: opening folder '{Folder}'", config.Imap.Folder);
            var folder = await client.GetFolderAsync(config.Imap.Folder, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            logger.LogDebug("Email delivery checker: folder opened ({MessageCount} messages)", folder.Count);

            // HashSet prevents double-flagging the same UID when subject filters overlap (Gap 5).
            var uidsToExpunge = new HashSet<UniqueId>();

            foreach (var check in config.Checks)
            {
                var checkState = await EvaluateCheckAsync(folder, check, now, uidsToExpunge, cancellationToken);

                // Carry forward ConsecutiveFailures (Gap 2 / Gap 3 cold-start)
                var prevFailures = previousStates?.GetValueOrDefault(check.Name)?.ConsecutiveFailures ?? 0;
                checkState.ConsecutiveFailures = checkState.Status == MetricStatus.Healthy ? 0 : prevFailures + 1;

                results[check.Name] = checkState;
            }

            if (uidsToExpunge.Count > 0)
            {
                logger.LogDebug("Email delivery checker: expunging {Count} probe message(s)", uidsToExpunge.Count);
                await folder.AddFlagsAsync(uidsToExpunge.ToList(), MessageFlags.Deleted, silent: true, cancellationToken);
                await folder.ExpungeAsync(cancellationToken);
                logger.LogDebug("Email delivery checker: expunge complete");
            }

            logger.LogDebug("Email delivery checker: closing folder");
            await folder.CloseAsync(expunge: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email delivery checker: IMAP connection to {Host}:{Port} failed", config.Imap.Host, config.Imap.Port);
            var imapError = ex.Message;

            // Fill any checks not yet evaluated with the IMAP error and increment consecutive failures
            foreach (var check in config.Checks)
            {
                if (!results.ContainsKey(check.Name))
                {
                    var prevFailures = previousStates?.GetValueOrDefault(check.Name)?.ConsecutiveFailures ?? 0;
                    results[check.Name] = new EmailDeliveryCheckState
                    {
                        CheckId = check.Name,
                        CheckName = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description,
                        LastChecked = now,
                        Status = MetricStatus.Critical,
                        ConsecutiveFailures = prevFailures + 1,
                        ErrorMessage = imapError,
                        IsConnectionError = true
                    };
                }
            }
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(quit: true, CancellationToken.None);
        }

        return results;
    }

    private async Task<EmailDeliveryCheckState> EvaluateCheckAsync(
        IMailFolder folder,
        EmailDeliveryCheckConfig check,
        DateTime now,
        HashSet<UniqueId> uidsToExpunge,
        CancellationToken cancellationToken)
    {
        var state = new EmailDeliveryCheckState
        {
            CheckId = check.Name,
            CheckName = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description,
            LastChecked = now
        };

        try
        {
            logger.LogDebug("Email delivery check '{CheckName}': searching for subject '{Filter}'",
                check.Name, check.SubjectFilter);
            var query = SearchQuery.SubjectContains(check.SubjectFilter);
            var uids = await folder.SearchAsync(query, cancellationToken);
            logger.LogDebug("Email delivery check '{CheckName}': found {Count} matching message(s)",
                check.Name, uids.Count);

            if (uids.Count == 0)
            {
                // No matching messages — distinct from "found but stale" (Gap 1)
                state.Status = MetricStatus.Critical;
                state.IsNotFound = true;
                state.ErrorMessage = $"Keine Nachrichten mit Filter '{check.SubjectFilter}' gefunden";
                logger.LogWarning("Email delivery check '{CheckName}': no messages found matching '{Filter}'",
                    check.Name, check.SubjectFilter);
                return state;
            }

            logger.LogDebug("Email delivery check '{CheckName}': fetching envelope for {Count} message(s)",
                check.Name, uids.Count);
            // Fetch Date header for all matching messages
            var items = await folder.FetchAsync(uids, MessageSummaryItems.Envelope, cancellationToken);
            var newest = items
                .Where(m => m.Envelope?.Date != null)
                .OrderByDescending(m => m.Envelope!.Date!.Value)
                .FirstOrDefault();

            if (newest == null)
            {
                state.Status = MetricStatus.Critical;
                state.IsNotFound = true;
                state.ErrorMessage = $"Nachrichten gefunden, aber Datum nicht lesbar (Filter: '{check.SubjectFilter}')";
                return state;
            }

            var messageDate = newest.Envelope!.Date!.Value.UtcDateTime;
            var ageMinutes = (now - messageDate).TotalMinutes;
            state.LastReceivedAt = messageDate;
            state.AgeMinutes = Math.Round(ageMinutes, 1);

            state.Status = EvaluateAgeStatus(ageMinutes, check.MaxAgeWarningMinutes, check.MaxAgeCriticalMinutes);

            logger.LogDebug("Email delivery check '{CheckName}': newest message age {AgeMinutes:F1} min → {Status}",
                check.Name, ageMinutes, state.Status);

            if (check.DeleteAfterCheck)
            {
                // Add all matching UIDs for expunge; HashSet deduplicates if filters overlap (Gap 5)
                foreach (var uid in uids)
                    uidsToExpunge.Add(uid);
            }
        }
        catch (Exception ex)
        {
            state.Status = MetricStatus.Critical;
            state.IsConnectionError = true;
            state.ErrorMessage = ex.Message;
            logger.LogError(ex, "Email delivery check '{CheckName}': error during search/fetch", check.Name);
        }

        return state;
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
                _ => throw new InvalidOperationException($"Unsupported IMAP sslMode '{sslMode}'. Allowed: true, false, auto")
            };
        }

        return useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;
    }

    /// <summary>
    /// Determines delivery status from message age and configured thresholds.
    /// Exposed internal for unit testing.
    /// </summary>
    internal static MetricStatus EvaluateAgeStatus(double ageMinutes, int maxAgeWarningMinutes, int maxAgeCriticalMinutes)
        => ageMinutes < maxAgeWarningMinutes ? MetricStatus.Healthy
            : ageMinutes < maxAgeCriticalMinutes ? MetricStatus.Warning
            : MetricStatus.Critical;

    private string? DecryptCredential(string value, string masterKey)
    {
        if (!ConnectionStringEncryption.IsEncrypted(value))
            return value;

        if (string.IsNullOrEmpty(masterKey))
        {
            logger.LogWarning("Email delivery checker: IMAP password is encrypted but Encryption:ConnectionStringKey is not configured");
            return null;
        }

        return ConnectionStringEncryption.Decrypt(value, masterKey);
    }
}
