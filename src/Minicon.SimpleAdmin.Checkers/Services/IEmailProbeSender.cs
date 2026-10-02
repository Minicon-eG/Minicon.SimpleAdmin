using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Sends scheduled probe emails via SMTP to a shared monitoring mailbox.
/// Each probe is sent at its configured interval using a persisted last-attempt timestamp.
/// </summary>
public interface IEmailProbeSender
{
    /// <summary>
    /// Evaluates each probe: if the interval has elapsed since the last attempt,
    /// sends a probe email via SMTP and persists timestamps. Previous states are used
    /// to carry forward ConsecutiveFailures for status smoothing.
    /// </summary>
    Task<Dictionary<string, EmailProbeState>> SendPendingProbesAsync(
        List<EmailProbeConfig> probes,
        string masterKey,
        Dictionary<string, EmailProbeState>? previousStates = null,
        bool forceSend = false,
        CancellationToken cancellationToken = default);
}
