using System.ComponentModel.DataAnnotations;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.WebUI.ViewModels;

/// <summary>
/// Form model for creating/editing a <see cref="Domain"/>.
/// </summary>
public sealed class DomainViewModel
{
    /// <summary>Name before editing (null when creating)</summary>
    public string? OriginalName { get; set; }

    [Required(ErrorMessage = "Der Domainname ist erforderlich.")]
    [RegularExpression(DnsNamePattern, ErrorMessage = "Ungültiger DNS-Name (erlaubt: Buchstaben, Ziffern, '-' und '.').")]
    [StringLength(253)]
    [Display(Name = "Domainname")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte eine Umgebung wählen.")]
    [Display(Name = "Umgebung")]
    public string Environment { get; set; } = string.Empty;

    public List<string> Servers { get; set; } = new();

    [StringLength(200)]
    [Display(Name = "NetScaler-vServer")]
    public string? NetScalerVServer { get; set; }

    [StringLength(200)]
    [Display(Name = "Health Check")]
    public string? HealthCheck { get; set; }

    [StringLength(500)]
    [Display(Name = "Beschreibung")]
    public string? Description { get; set; }

    [Display(Name = "Ausnahme zulassen")]
    public bool AllowNameMismatch { get; set; }

    // ── Display data (not posted) ──────────────────────────────────────────

    public List<string> Environments { get; set; } = new();

    /// <summary>Environment key → server names of that environment</summary>
    public Dictionary<string, List<string>> ServersByEnvironment { get; set; } = new();

    public Dictionary<string, string?> ServerDescriptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> HealthCheckNames { get; set; } = new();

    public List<string> VServerNames { get; set; } = new();

    /// <summary>Effective suffix map (for the hint text)</summary>
    public IReadOnlyDictionary<string, string> SuffixMap { get; set; } = new Dictionary<string, string>();

    internal const string DnsNamePattern =
        @"^\s*[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?\s*$";

    public static DomainViewModel FromDomain(Domain domain) => new()
    {
        OriginalName = domain.Name,
        Name = domain.Name,
        Environment = domain.Environment,
        Servers = domain.Servers.ToList(),
        NetScalerVServer = domain.NetScalerVServer,
        HealthCheck = domain.HealthCheck,
        Description = domain.Description,
        AllowNameMismatch = domain.AllowNameMismatch
    };

    public Domain ToDomain() => new()
    {
        Name = Name.Trim().TrimEnd('.').ToLowerInvariant(),
        Environment = Environment.Trim(),
        Servers = Servers
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        NetScalerVServer = string.IsNullOrWhiteSpace(NetScalerVServer) ? null : NetScalerVServer.Trim(),
        HealthCheck = string.IsNullOrWhiteSpace(HealthCheck) ? null : HealthCheck.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        AllowNameMismatch = AllowNameMismatch
    };
}

/// <summary>
/// One row of the domain list.
/// </summary>
public sealed class DomainListItem
{
    public required Domain Domain { get; init; }
    public required DomainConventionView Convention { get; init; }
    public bool EnvironmentExists { get; init; }
    public List<string> ServersOutsideEnvironment { get; init; } = new();
    public List<string> UnknownServers { get; init; } = new();
}

/// <summary>
/// Convention result for views (see <c>DomainNamingConvention.Evaluate</c>).
/// </summary>
public sealed record DomainConventionView(string? SuggestedEnvironment, bool IsConsistent, bool ShowWarning);

/// <summary>
/// Domain list page model.
/// </summary>
public sealed class DomainIndexViewModel
{
    public List<string> Environments { get; set; } = new();
    public List<DomainListItem> Items { get; set; } = new();
    public IReadOnlyDictionary<string, string> SuffixMap { get; set; } = new Dictionary<string, string>();
}
