using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks that probe emails from configured senders have arrived in a shared IMAP mailbox.
/// Opens one IMAP connection per check cycle and runs all subject-filter searches within it.
/// </summary>
public interface IEmailDeliveryChecker
{
    /// <summary>
    /// Connects to the configured IMAP mailbox once and evaluates all delivery checks.
    /// Each check searches for the newest message matching its subject filter and
    /// compares the message age against the configured warning/critical thresholds.
    /// Previous states are used to carry forward ConsecutiveFailures for status smoothing.
    /// </summary>
    Task<Dictionary<string, EmailDeliveryCheckState>> CheckDeliveryAsync(
        ServerEmailDeliveryConfig config,
        string masterKey,
        Dictionary<string, EmailDeliveryCheckState>? previousStates = null,
        CancellationToken cancellationToken = default);
}
