using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class EnvironmentsController : Controller
{
    private readonly ConfigurationService _configService;

    public EnvironmentsController(ConfigurationService configService)
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

        var allServers = _configService.GetServers().ToList();
        var activeServers = allServers.Where(s => s.Active).ToList();

        var model = new EnvironmentMatrixViewModel
        {
            Environments = _configService.GetEnvironmentNames().ToList(),
            // Only show active servers in pool
            AvailableServers = activeServers.Select(s => s.Name).ToList(),
            Categories = new[] { "Transfer", "Services", "Biztalk", "Database" },
            // Include ALL servers for descriptions (also inactive ones already assigned)
            ServerDescriptions = allServers.ToDictionary(s => s.Name, s => s.Description)
        };

        // Load server assignments for each environment and category
        foreach (var env in model.Environments)
        {
            model.ServerAssignments[env] = new Dictionary<string, List<string>>();
            foreach (var category in model.Categories)
            {
                model.ServerAssignments[env][category] = _configService.GetEnvironmentServers(env, category);
            }
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> UpdateAssignmentAjax([FromBody] UpdateAssignmentRequest request)
    {
        try
        {
            await _configService.MutateAndSaveAsync(() =>
                _configService.SetEnvironmentServers(request.Environment, request.Category, request.Servers ?? new List<string>()));
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignment(string environment, string category, string[] servers)
    {
        try
        {
            await _configService.MutateAndSaveAsync(() =>
                _configService.SetEnvironmentServers(environment, category, servers?.ToList() ?? new List<string>()));
            TempData["Success"] = $"Serverzuordnung für {environment}/{category} aktualisiert.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEnvironment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Umgebungsname darf nicht leer sein.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _configService.MutateAndSaveAsync(() =>
            {
                foreach (var cat in new[] { "Transfer", "Services", "Biztalk", "Database" })
                    _configService.SetEnvironmentServers(name, cat, new List<string>());
            });
            TempData["Success"] = $"Umgebung '{name}' wurde erstellt.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEnvironment(string name)
    {
        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.Config.Environments.Remove(name));
            TempData["Success"] = $"Umgebung '{name}' wurde gelöscht.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }
}

public class EnvironmentMatrixViewModel
{
    public List<string> Environments { get; set; } = new();
    public List<string> AvailableServers { get; set; } = new();
    public string[] Categories { get; set; } = Array.Empty<string>();
    public Dictionary<string, Dictionary<string, List<string>>> ServerAssignments { get; set; } = new();
    public Dictionary<string, string?> ServerDescriptions { get; set; } = new();
}

public class UpdateAssignmentRequest
{
    public string Environment { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string>? Servers { get; set; }
}
