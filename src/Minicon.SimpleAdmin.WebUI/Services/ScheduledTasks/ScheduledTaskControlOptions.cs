using Microsoft.Extensions.Configuration;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// Host-side settings of the scheduled-task remote control (<c>appsettings.json</c> section
/// <c>ScheduledTasks</c>). Deliberately NOT part of config.json: whoever can edit the configuration
/// in the UI must not be able to re-enable control on a host that opted out.
/// </summary>
public sealed class ScheduledTaskControlOptions
{
    public const string SectionName = "ScheduledTasks";

    /// <summary>
    /// Master switch for enable/disable/run/end (default on; a host can opt out with <c>false</c>).
    /// Forced off while the WebUI runs without authentication, unless <see cref="AllowWithoutAuthentication"/> is set.
    /// The service itself still allows <see cref="IScheduledTaskControl.QueryAsync"/>, but the REST API
    /// also blocks the live query while control is off (it contacts remote servers as well); the
    /// worker-reported state stays readable either way.
    /// </summary>
    public bool ControlEnabled { get; set; } = true;

    /// <summary>
    /// Keep the control on even with <c>Authentication:Enabled=false</c> (isolated dev/test hosts only —
    /// then anyone reaching the WebUI can switch tasks on the target servers).
    /// </summary>
    public bool AllowWithoutAuthentication { get; set; }

    /// <summary>PowerShell executable (default: <c>powershell.exe</c> on Windows, <c>pwsh</c> elsewhere).</summary>
    public string? PowerShellPath { get; set; }

    /// <summary>Maximum duration of one PowerShell call incl. connecting to the target server (default 60 s, 5…600).</summary>
    public int ActionTimeoutSeconds { get; set; } = 60;

    /// <summary>Directory of <c>scheduled-task-actions.json</c> (default: the config directory).</summary>
    public string? AuditLogDirectory { get; set; }

    /// <summary>Number of audit entries kept (default 2000, minimum 10).</summary>
    public int AuditHistorySize { get; set; } = 2000;

    /// <summary>Why the control is off (shown in the UI); null when enabled. Never bound from configuration.</summary>
    public string? DisabledReason { get; internal set; }

    internal const string AuditFileName = "scheduled-task-actions.json";

    /// <summary>
    /// Reads the <c>ScheduledTasks</c> section and applies the authentication rule: without
    /// authentication the control is switched off unless explicitly allowed.
    /// </summary>
    internal static ScheduledTaskControlOptions Resolve(IConfiguration configuration, bool authenticationEnabled)
    {
        var options = configuration.GetSection(SectionName).Get<ScheduledTaskControlOptions>() ?? new ScheduledTaskControlOptions();
        if (!options.ControlEnabled)
        {
            options.DisabledReason = "Die Steuerung geplanter Aufgaben ist auf diesem Host abgeschaltet (\"ScheduledTasks\": { \"ControlEnabled\": false } in der appsettings.json der WebUI). Der Zustand kann weiterhin angezeigt werden.";
        }
        else if (!authenticationEnabled && !options.AllowWithoutAuthentication)
        {
            options.ControlEnabled = false;
            options.DisabledReason = "Die Steuerung geplanter Aufgaben ist automatisch abgeschaltet, weil die WebUI ohne Anmeldung läuft (\"Authentication\": { \"Enabled\": false }) — sonst könnte jeder, der die WebUI erreicht, Aufgaben auf den Servern starten, beenden oder deaktivieren. Nur für isolierte Test-Hosts: \"ScheduledTasks\": { \"AllowWithoutAuthentication\": true }.";
        }
        return options;
    }

    internal string EffectivePowerShellPath =>
        !string.IsNullOrWhiteSpace(PowerShellPath) ? PowerShellPath! : OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";

    internal TimeSpan EffectiveActionTimeout => TimeSpan.FromSeconds(Math.Clamp(ActionTimeoutSeconds, 5, 600));

    internal int EffectiveAuditHistorySize => Math.Max(10, AuditHistorySize);
}
