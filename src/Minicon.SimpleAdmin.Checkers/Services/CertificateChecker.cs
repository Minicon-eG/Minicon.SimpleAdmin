using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Checks certificate expiration from Windows Certificate Store or IIS
/// Falls back to empty results on non-Windows or when certificates are unavailable
/// </summary>
public class CertificateChecker : ICertificateChecker
{
    private readonly ILogger<CertificateChecker> _logger;
    private readonly bool _isWindows;

    public CertificateChecker(ILogger<CertificateChecker> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Checks all configured certificates for a server
    /// </summary>
    public Task<Dictionary<string, CertificateState>> CheckCertificatesAsync(
        CertificateChecksConfig config,
        CertificateDefaults defaults)
    {
        var results = new Dictionary<string, CertificateState>();

        if (!_isWindows)
        {
            _logger.LogDebug("Not running on Windows, returning empty Certificate results");
            return Task.FromResult(results);
        }

        if (!config.Enabled || config.Checks.Count == 0)
        {
            return Task.FromResult(results);
        }

        return CheckCertificatesInternalAsync(config, defaults);
    }

    /// <summary>
    /// Internal method to check multiple certificates — returns one entry per matching X509 certificate
    /// (keyed by thumbprint). A single check config may yield 0..N entries.
    /// </summary>
    private Task<Dictionary<string, CertificateState>> CheckCertificatesInternalAsync(
        CertificateChecksConfig config,
        CertificateDefaults defaults)
    {
        var results = new Dictionary<string, CertificateState>(StringComparer.OrdinalIgnoreCase);

        foreach (var certConfig in config.Checks)
        {
            var effectiveWarningDays = certConfig.WarningDays ?? defaults.WarningDays;
            var effectiveCriticalDays = certConfig.CriticalDays ?? defaults.CriticalDays;

            List<CertificateState> states;
            try
            {
                states = EvaluateCertificateSource(certConfig, effectiveWarningDays, effectiveCriticalDays);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking certificate from {Source}", certConfig.Source);
                results[$"error:{certConfig.Source}"] = new CertificateState
                {
                    Subject = $"Fehler: {certConfig.Source}",
                    Status = MetricStatus.Unknown
                };
                continue;
            }

            if (states.Count == 0)
            {
                _logger.LogWarning("No certificates matched filter for source {Source}", certConfig.Source);
                results[$"nomatch:{certConfig.Source}:{certConfig.Filter?.SubjectContains}"] = new CertificateState
                {
                    Subject = $"Keine Übereinstimmung: {certConfig.Filter?.SubjectContains ?? certConfig.Source}",
                    Status = MetricStatus.Unknown
                };
                continue;
            }

            foreach (var state in states)
            {
                var key = !string.IsNullOrEmpty(state.Thumbprint) ? state.Thumbprint : $"{certConfig.Source}:{state.Subject}";
                results[key] = state;
            }
        }

        return Task.FromResult(results);
    }

    /// <summary>
    /// Evaluates one configured check source and returns one CertificateState per matching X509 certificate.
    /// </summary>
    private List<CertificateState> EvaluateCertificateSource(
        CertificateCheckConfig config,
        int warningDays,
        int criticalDays)
    {
        var result = new List<CertificateState>();
        if (!_isWindows)
        {
            return result;
        }

        var certificates = GetCertificatesFromSource(config.Source, config.Filter);
        foreach (var cert in certificates)
        {
            var state = new CertificateState
            {
                Thumbprint = cert.Thumbprint,
                Subject = cert.Subject,
                ExpiresAt = cert.NotAfter,
                DaysUntilExpiry = (int)(cert.NotAfter - DateTime.Now).TotalDays
            };

            if (state.DaysUntilExpiry <= criticalDays)
            {
                state.Status = MetricStatus.Critical;
            }
            else if (state.DaysUntilExpiry <= warningDays)
            {
                state.Status = MetricStatus.Warning;
            }
            else
            {
                state.Status = MetricStatus.Healthy;
            }

            _logger.LogDebug("Certificate {Subject}: {Days} days until expiry, status: {Status}",
                state.Subject, state.DaysUntilExpiry, state.Status);
            result.Add(state);
        }

        return result;
    }

    /// <summary>
    /// Backward-compatible single-certificate check — returns the first matching certificate's state.
    /// </summary>
    public Task<CertificateState> CheckCertificateAsync(
        CertificateCheckConfig config,
        int warningDays,
        int criticalDays)
    {
        if (!_isWindows)
        {
            return Task.FromResult(new CertificateState { Status = MetricStatus.Unknown });
        }

        try
        {
            var states = EvaluateCertificateSource(config, warningDays, criticalDays);
            return Task.FromResult(states.FirstOrDefault() ?? new CertificateState { Status = MetricStatus.Unknown });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking certificate from {Source}", config.Source);
            return Task.FromResult(new CertificateState { Status = MetricStatus.Unknown });
        }
    }

    /// <summary>
    /// Gets certificates from a specified source
    /// </summary>
    private List<X509Certificate2> GetCertificatesFromSource(string source, CertificateFilter? filter)
    {
        var certificates = new List<X509Certificate2>();

        try
        {
            if (source.StartsWith("store://", StringComparison.OrdinalIgnoreCase))
            {
                // Parse store location and name
                // Format: store://LocalMachine/My or store://CurrentUser/TrustedRoot
                var parts = source.Substring(8).Split('/');
                if (parts.Length >= 2)
                {
                    var location = ParseStoreLocation(parts[0]);
                    var storeName = ParseStoreName(parts[1]);

                    certificates = GetCertificatesFromStore(location, storeName, filter);
                }
            }
            else if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                // Read from file
                var filePath = source.Substring(7);
                if (File.Exists(filePath))
                {
                    var cert = new X509Certificate2(filePath);
                    certificates.Add(cert);
                }
            }
            else if (source.Equals("iis://", StringComparison.OrdinalIgnoreCase))
            {
                // Get certificates from IIS
                certificates = GetCertificatesFromIIS(filter);
            }
            else
            {
                _logger.LogWarning("Unknown certificate source: {Source}", source);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting certificates from {Source}", source);
        }

        return certificates;
    }

    /// <summary>
    /// Gets certificates from a Windows Certificate Store
    /// </summary>
    private List<X509Certificate2> GetCertificatesFromStore(
        StoreLocation location,
        StoreName storeName,
        CertificateFilter? filter)
    {
        var certificates = new List<X509Certificate2>();

        try
        {
            using var store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly);

            foreach (var cert in store.Certificates)
            {
                if (filter != null && !MatchesFilter(cert, filter))
                {
                    continue;
                }

                certificates.Add(cert);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading certificate store {Location}/{Name}", location, storeName);
        }

        return certificates;
    }

    /// <summary>
    /// Gets certificates from IIS (current bindings)
    /// </summary>
    private List<X509Certificate2> GetCertificatesFromIIS(CertificateFilter? filter)
    {
        var certificates = new List<X509Certificate2>();

        try
        {
            // Try to get IIS bindings using appcmd
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "http show sslcert",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
                process.WaitForExitAsync().GetAwaiter().GetResult();

                // Parse SSL certificate hashes from output
                // This is a simplified approach - in production you might use Microsoft.Web.Administration
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting IIS certificates");
        }

        return certificates;
    }

    /// <summary>
    /// Checks if a certificate matches the filter criteria
    /// </summary>
    private static bool MatchesFilter(X509Certificate2 cert, CertificateFilter filter)
    {
        if (!string.IsNullOrEmpty(filter.SubjectContains))
        {
            if (!cert.Subject.Contains(filter.SubjectContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(filter.Thumbprint))
        {
            if (!cert.Thumbprint.Equals(filter.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(filter.FriendlyNameContains))
        {
            if (!cert.FriendlyName.Contains(filter.FriendlyNameContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(filter.IssuerContains))
        {
            if (!cert.Issuer.Contains(filter.IssuerContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses a store location string to StoreLocation enum
    /// </summary>
    private static StoreLocation ParseStoreLocation(string location)
    {
        return location.ToLowerInvariant() switch
        {
            "localmachine" => StoreLocation.LocalMachine,
            "currentuser" => StoreLocation.CurrentUser,
            _ => StoreLocation.LocalMachine
        };
    }

    /// <summary>
    /// Parses a store name string to StoreName enum
    /// </summary>
    private static StoreName ParseStoreName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "my" => StoreName.My,
            "trustedroot" => StoreName.Root,
            "trustedpeople" => StoreName.TrustedPeople,
            "trustedpublisher" => StoreName.TrustedPublisher,
            "ca" => StoreName.CertificateAuthority,
            "authroot" => StoreName.AuthRoot,
            "addressbook" => StoreName.AddressBook,
            "disallowed" => StoreName.Disallowed,
            _ => StoreName.My
        };
    }
}
