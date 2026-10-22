using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Result of checking a domain against the naming convention.
/// </summary>
/// <param name="SuggestedEnvironment">Environment key derived from the name, or null when no suggestion is possible</param>
/// <param name="MatchedSuffix">The recognised suffix (e.g. <c>-dev</c>), or null for a name without a known suffix</param>
/// <param name="IsConsistent">True when the configured environment matches the suggestion (or there is no suggestion)</param>
/// <param name="ShowWarning">True when the domain is inconsistent and no exception (<see cref="Domain.AllowNameMismatch"/>) is set</param>
public sealed record DomainConventionResult(
    string? SuggestedEnvironment,
    string? MatchedSuffix,
    bool IsConsistent,
    bool ShowWarning);

/// <summary>
/// Naming convention for domains: <c>&lt;name&gt;-dev</c> → development, <c>&lt;name&gt;-ref</c> → ref,
/// <c>&lt;name&gt;-prod</c> and <c>&lt;name&gt;</c> (no known suffix) → production. Extensible via
/// <see cref="Config.DomainSuffixMap"/>. The convention only suggests and warns; the environment of a
/// domain is always the explicitly configured <see cref="Domain.Environment"/>.
/// </summary>
public static class DomainNamingConvention
{
    /// <summary>
    /// Environment key suggested for names without a known suffix (only if this key exists).
    /// </summary>
    public const string ProductionEnvironment = "production";

    /// <summary>
    /// Built-in suffix → environment key map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultSuffixMap { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["-dev"] = "development",
            ["-ref"] = "ref",
            ["-prod"] = ProductionEnvironment
        };

    /// <summary>
    /// Built-in map merged with the optional override map of the config (override wins,
    /// suffixes are normalised to a leading <c>-</c>; an empty value removes a built-in suffix).
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetEffectiveSuffixMap(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>(DefaultSuffixMap, StringComparer.OrdinalIgnoreCase);
        if (overrides == null) return map;

        foreach (var (rawSuffix, env) in overrides)
        {
            var suffix = NormalizeSuffix(rawSuffix);
            if (suffix == null) continue;
            if (string.IsNullOrWhiteSpace(env)) map.Remove(suffix);
            else map[suffix] = env.Trim();
        }
        return map;
    }

    /// <summary>
    /// Suggests the environment for a domain name. Only the first DNS label is considered
    /// (<c>app-dev.example.org</c> → <c>app-dev</c>). Returns the key in the casing used in
    /// <paramref name="environmentKeys"/>, or null when the derived environment does not exist.
    /// </summary>
    public static string? SuggestEnvironment(
        string? domainName,
        IEnumerable<string> environmentKeys,
        IReadOnlyDictionary<string, string>? suffixMapOverrides = null)
        => Analyze(domainName, environmentKeys, suffixMapOverrides).Environment;

    /// <summary>
    /// Suggests the environment for a domain name using the environments and suffix map of <paramref name="config"/>.
    /// </summary>
    public static string? SuggestEnvironment(string? domainName, Config config)
        => SuggestEnvironment(domainName, config.Environments.Keys, config.DomainSuffixMap);

    /// <summary>
    /// True when the configured environment of <paramref name="domain"/> matches the suggestion
    /// derived from its name, or when no suggestion is possible. Ignores <see cref="Domain.AllowNameMismatch"/>.
    /// </summary>
    public static bool IsConsistent(
        Domain domain,
        IEnumerable<string> environmentKeys,
        IReadOnlyDictionary<string, string>? suffixMapOverrides = null)
        => Evaluate(domain, environmentKeys, suffixMapOverrides).IsConsistent;

    /// <summary>
    /// <see cref="IsConsistent(Domain, IEnumerable{string}, IReadOnlyDictionary{string, string}?)"/> against <paramref name="config"/>.
    /// </summary>
    public static bool IsConsistent(Domain domain, Config config)
        => IsConsistent(domain, config.Environments.Keys, config.DomainSuffixMap);

    /// <summary>
    /// Checks <paramref name="domain"/> against the convention.
    /// </summary>
    public static DomainConventionResult Evaluate(
        Domain domain,
        IEnumerable<string> environmentKeys,
        IReadOnlyDictionary<string, string>? suffixMapOverrides = null)
    {
        var (suggested, suffix) = Analyze(domain.Name, environmentKeys, suffixMapOverrides);
        var consistent = suggested == null
            || string.Equals(suggested, domain.Environment?.Trim(), StringComparison.OrdinalIgnoreCase);
        return new DomainConventionResult(suggested, suffix, consistent, !consistent && !domain.AllowNameMismatch);
    }

    /// <summary>
    /// <see cref="Evaluate(Domain, IEnumerable{string}, IReadOnlyDictionary{string, string}?)"/> against <paramref name="config"/>.
    /// </summary>
    public static DomainConventionResult Evaluate(Domain domain, Config config)
        => Evaluate(domain, config.Environments.Keys, config.DomainSuffixMap);

    private static (string? Environment, string? Suffix) Analyze(
        string? domainName,
        IEnumerable<string> environmentKeys,
        IReadOnlyDictionary<string, string>? suffixMapOverrides)
    {
        if (string.IsNullOrWhiteSpace(domainName)) return (null, null);

        var label = domainName.Trim().Split('.')[0];
        var keys = environmentKeys.ToList();
        var map = GetEffectiveSuffixMap(suffixMapOverrides);

        // Longest suffix first, so e.g. "-preprod" wins over "-prod".
        var suffix = map.Keys
            .Where(s => label.Length > s.Length && label.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Length)
            .FirstOrDefault();

        var targetEnv = suffix != null ? map[suffix] : ProductionEnvironment;
        var existing = keys.FirstOrDefault(k => string.Equals(k, targetEnv, StringComparison.OrdinalIgnoreCase));
        return (existing, suffix);
    }

    private static string? NormalizeSuffix(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return null;
        var trimmed = suffix.Trim();
        return trimmed.StartsWith('-') ? trimmed : "-" + trimmed;
    }
}
