namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Certificate expiration monitoring feature configuration
/// </summary>
public class CertificateFeatureConfig
{
    /// <summary>
    /// Whether the feature is enabled globally (default: false - opt-in)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional: Custom check interval in seconds (null = use global interval)
    /// </summary>
    public int? CheckIntervalSeconds { get; set; }

    /// <summary>
    /// Default settings for certificate monitoring
    /// </summary>
    public CertificateDefaults Defaults { get; set; } = new();
}

/// <summary>
/// Default settings for certificate monitoring
/// </summary>
public class CertificateDefaults
{
    /// <summary>
    /// Days before expiration to trigger warning (default: 30)
    /// </summary>
    public int WarningDays { get; set; } = 30;

    /// <summary>
    /// Days before expiration to trigger critical (default: 14)
    /// </summary>
    public int CriticalDays { get; set; } = 14;
}

/// <summary>
/// Server-specific certificate checks configuration
/// </summary>
public class CertificateChecksConfig
{
    /// <summary>
    /// Whether certificate checks are enabled for this server (default: false)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// List of certificate sources to check
    /// </summary>
    public List<CertificateCheckConfig> Checks { get; set; } = new();
}

/// <summary>
/// Configuration for a specific certificate check
/// </summary>
public class CertificateCheckConfig
{
    /// <summary>
    /// Certificate source (e.g., "store://LocalMachine/My", "iis://", "file://path/to/cert.pfx")
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Optional filter for certificates
    /// </summary>
    public CertificateFilter? Filter { get; set; }

    /// <summary>
    /// Days before expiration to trigger warning (overrides global default)
    /// </summary>
    public int? WarningDays { get; set; }

    /// <summary>
    /// Days before expiration to trigger critical (overrides global default)
    /// </summary>
    public int? CriticalDays { get; set; }
}

/// <summary>
/// Filter criteria for certificates
/// </summary>
public class CertificateFilter
{
    /// <summary>
    /// Subject must contain this string
    /// </summary>
    public string? SubjectContains { get; set; }

    /// <summary>
    /// Thumbprint must match exactly
    /// </summary>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// Friendly name must contain this string
    /// </summary>
    public string? FriendlyNameContains { get; set; }

    /// <summary>
    /// Issuer must contain this string
    /// </summary>
    public string? IssuerContains { get; set; }
}
