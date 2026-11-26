using Microsoft.Extensions.Configuration;

namespace Minicon.SimpleAdmin.WebUI.Services.Backup;

public sealed class SettingsBackupOptions
{
    public const string SectionName = "Backup";
    public bool RestoreEnabled { get; set; } = true;
    public bool AllowWithoutAuthentication { get; set; }
    public string? DisabledReason { get; private set; }

    internal static SettingsBackupOptions Resolve(IConfiguration configuration, bool authenticationEnabled)
    {
        var options = configuration.GetSection(SectionName).Get<SettingsBackupOptions>() ?? new();
        if (!options.RestoreEnabled)
            options.DisabledReason = "Das Einspielen ist auf diesem Host abgeschaltet (Backup:RestoreEnabled=false).";
        else if (!authenticationEnabled && !options.AllowWithoutAuthentication)
        {
            options.RestoreEnabled = false;
            options.DisabledReason = "Das Einspielen ist ohne Anmeldung abgeschaltet. Nur für isolierte Test-Hosts: Backup:AllowWithoutAuthentication=true.";
        }
        return options;
    }
}
