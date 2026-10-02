using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Interface for checking certificate expiration
/// </summary>
public interface ICertificateChecker
{
    /// <summary>
    /// Checks all configured certificates for a server
    /// </summary>
    Task<Dictionary<string, CertificateState>> CheckCertificatesAsync(
        CertificateChecksConfig config,
        CertificateDefaults defaults);

    /// <summary>
    /// Checks a specific certificate
    /// </summary>
    Task<CertificateState> CheckCertificateAsync(
        CertificateCheckConfig config,
        int warningDays,
        int criticalDays);
}
