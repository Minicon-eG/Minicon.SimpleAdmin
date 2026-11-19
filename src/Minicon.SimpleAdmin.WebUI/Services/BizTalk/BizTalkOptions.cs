using Microsoft.Extensions.Configuration;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

/// <summary>Host settings, never taken from browser input or config.json.</summary>
public sealed class BizTalkOptions
{
    public const string SectionName = "BizTalk";
    public bool ControlEnabled { get; set; } = true;
    public bool AllowWithoutAuthentication { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 30;
    public int MaxInstances { get; set; } = 1000;
    public long MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Explicit API hostname approval per canonical config server ID.</summary>
    public Dictionary<string, string> AllowedApiHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DisabledReason { get; private set; }
    public int EffectiveMaxInstances => Math.Clamp(MaxInstances, 1, 5000);
    public long EffectiveMaxResponseBytes => Math.Clamp(MaxResponseBytes, 1, 8L * 1024 * 1024);
    public TimeSpan QueryTimeout(int? serverSeconds) => TimeSpan.FromSeconds(
        Math.Min(Math.Clamp(QueryTimeoutSeconds, 5, 120), serverSeconds is > 0 ? serverSeconds.Value : 30));

    internal static BizTalkOptions Resolve(IConfiguration configuration, bool authenticationEnabled)
    {
        var options = configuration.GetSection(SectionName).Get<BizTalkOptions>() ?? new();
        if (!options.ControlEnabled)
            options.DisabledReason = "Die Live-Abfrage ist auf diesem Host abgeschaltet (BizTalk:ControlEnabled=false).";
        else if (!authenticationEnabled && !options.AllowWithoutAuthentication)
        {
            options.ControlEnabled = false;
            options.DisabledReason = "Die Live-Abfrage ist ohne Anmeldung abgeschaltet. Nur für isolierte Test-Hosts: BizTalk:AllowWithoutAuthentication=true.";
        }
        return options;
    }
}
