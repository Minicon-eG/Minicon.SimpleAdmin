using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class SettingsController : Controller
{
    private readonly ConfigurationService _configService;

    public SettingsController(ConfigurationService configService)
    {
        _configService = configService;
    }

    public IActionResult Index()
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var output = _configService.GetOutputSettings();
        // Ensure CentralGenerator is initialized for form binding
        output.CentralGenerator ??= new CentralGeneratorSettings();

        var model = new SettingsViewModel
        {
            History = _configService.GetHistorySettings(),
            Output = output
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SettingsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _configService.MutateAndSaveAsync(() =>
            {
                _configService.UpdateHistorySettings(model.History);
                _configService.UpdateOutputSettings(model.Output);
            });
            TempData["Success"] = "Einstellungen wurden erfolgreich aktualisiert.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler beim Speichern: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    #region Features

    public IActionResult Features()
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var model = new FeaturesViewModel
        {
            Features = _configService.GetFeatures(),
            AvailableServers = _configService.GetServers().Where(s => s.Active).Select(s => s.Name).ToList()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Features(FeaturesViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Drop event log ignore rules without any match criteria (empty form rows) —
        // the checker treats them as inactive anyway, so don't persist them.
        model.Features.EventLog.IgnoreRules = model.Features.EventLog.IgnoreRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Source)
                        || r.EventIds.Count > 0
                        || !string.IsNullOrWhiteSpace(r.MessageContains))
            .ToList();

        // The notifier URLs are derived at runtime and no longer in the form. Preserve any existing
        // override values from the loaded config so a UI save doesn't wipe them.
        model.Features.Notifications ??= new NotificationsFeatureConfig();
        var existingNotif = _configService.GetFeatures().Notifications;
        model.Features.Notifications.StatusUrls = existingNotif.StatusUrls;
        model.Features.Notifications.PeerNotifyStateUrls = existingNotif.PeerNotifyStateUrls;
        model.Features.Notifications.AcknowledgesUrl = existingNotif.AcknowledgesUrl;
        model.Features.Notifications.WebUiBaseUrl = existingNotif.WebUiBaseUrl;
        model.Features.Notifications.LocalNotifyStatePath = existingNotif.LocalNotifyStatePath;

        // Report times come from one text field ("07:00, 13:00") — store them as a clean list.
        model.Features.Notifications.StatusReport ??= new StatusReportConfig();
        model.Features.Notifications.StatusReport.Times = model.Features.Notifications.StatusReport.Times
            .SelectMany(t => (t ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.UpdateFeatures(model.Features));
            TempData["Success"] = "Feature-Einstellungen wurden erfolgreich aktualisiert.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler beim Speichern: {ex.Message}";
        }

        return RedirectToAction(nameof(Features));
    }

    #endregion

    #region TimeProfiles

    public IActionResult TimeProfiles()
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var model = new TimeProfilesViewModel
        {
            TimeProfiles = _configService.GetTimeProfiles()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TimeProfiles(TimeProfilesViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.UpdateTimeProfiles(model.TimeProfiles));
            TempData["Success"] = "Zeitprofil-Einstellungen wurden erfolgreich aktualisiert.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler beim Speichern: {ex.Message}";
        }

        return RedirectToAction(nameof(TimeProfiles));
    }

    #endregion
}

public class SettingsViewModel
{
    public HistorySettings History { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
}

public class FeaturesViewModel
{
    public FeaturesConfig Features { get; set; } = new();

    /// <summary>Active server names, for the notifier server multi-select + WebUI-server dropdown.</summary>
    public List<string> AvailableServers { get; set; } = new();
}

public class TimeProfilesViewModel
{
    public TimeProfilesConfig TimeProfiles { get; set; } = new();
}
