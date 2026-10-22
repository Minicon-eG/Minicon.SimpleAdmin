using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

/// <summary>
/// CRUD for <see cref="Domain"/> entries (DNS name → environment, servers, NetScaler vServer).
/// The environment is always chosen explicitly; the naming convention only pre-selects and warns.
/// </summary>
public class DomainsController : Controller
{
    private readonly ConfigurationService _configService;

    public DomainsController(ConfigurationService configService)
    {
        _configService = configService;
    }

    public IActionResult Index()
    {
        if (!_configService.IsLoaded) return NoConfig();

        var config = _configService.Config;
        var knownServers = config.Servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var model = new DomainIndexViewModel
        {
            Environments = OrderedEnvironments(),
            SuffixMap = DomainNamingConvention.GetEffectiveSuffixMap(config.DomainSuffixMap),
            Items = _configService.GetDomains().Select(d =>
            {
                var envExists = EnvironmentKey(d.Environment) != null;
                var envServers = envExists
                    ? _configService.GetServerNamesInEnvironment(EnvironmentKey(d.Environment)!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return new DomainListItem
                {
                    Domain = d,
                    Convention = ToView(DomainNamingConvention.Evaluate(d, config)),
                    EnvironmentExists = envExists,
                    UnknownServers = d.Servers.Where(s => !knownServers.Contains(s)).ToList(),
                    ServersOutsideEnvironment = d.Servers.Where(s => knownServers.Contains(s) && !envServers.Contains(s)).ToList()
                };
            }).ToList()
        };

        return View(model);
    }

    public IActionResult Create(string? environment = null)
    {
        if (!_configService.IsLoaded) return NoConfig();

        var model = new DomainViewModel { Environment = EnvironmentKey(environment) ?? string.Empty };
        return View(Populate(model));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DomainViewModel model)
    {
        if (!_configService.IsLoaded) return NoConfig();

        model.OriginalName = null;
        var domain = Validate(model);
        if (!ModelState.IsValid) return View(Populate(model));

        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.AddDomain(domain));
            TempData["Success"] = $"Domain '{domain.Name}' wurde angelegt.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(Populate(model));
        }
    }

    public IActionResult Edit(string id)
    {
        if (!_configService.IsLoaded) return NoConfig();

        var domain = _configService.GetDomain(id);
        if (domain == null)
        {
            TempData["Error"] = $"Domain '{id}' nicht gefunden.";
            return RedirectToAction(nameof(Index));
        }

        return View(Populate(DomainViewModel.FromDomain(domain)));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(DomainViewModel model)
    {
        if (!_configService.IsLoaded) return NoConfig();

        if (string.IsNullOrEmpty(model.OriginalName) || _configService.GetDomain(model.OriginalName) == null)
        {
            TempData["Error"] = $"Domain '{model.OriginalName}' nicht gefunden.";
            return RedirectToAction(nameof(Index));
        }

        var domain = Validate(model);
        if (!ModelState.IsValid) return View(Populate(model));

        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.UpdateDomain(model.OriginalName!, domain));
            TempData["Success"] = $"Domain '{domain.Name}' wurde gespeichert.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(Populate(model));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.DeleteDomain(id));
            TempData["Success"] = $"Domain '{id}' wurde gelöscht.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Fehler: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Live convention check for the form: suggested environment for a name and whether the
    /// chosen environment matches it.
    /// </summary>
    [HttpGet]
    public IActionResult Suggest(string? name, string? environment)
    {
        var config = _configService.Config;
        var result = DomainNamingConvention.Evaluate(
            new Domain { Name = name ?? string.Empty, Environment = environment ?? string.Empty }, config);
        return Json(new { suggested = result.SuggestedEnvironment, suffix = result.MatchedSuffix, consistent = result.IsConsistent });
    }

    /// <summary>
    /// Validates the form and returns the normalised domain. Adds model errors for an unknown
    /// environment, duplicate names, servers outside the environment and an unconfirmed naming mismatch.
    /// </summary>
    internal Domain Validate(DomainViewModel model)
    {
        var domain = model.ToDomain();
        var config = _configService.Config;

        var envKey = EnvironmentKey(domain.Environment);
        if (!string.IsNullOrWhiteSpace(domain.Environment) && envKey == null)
        {
            ModelState.AddModelError(nameof(model.Environment), $"Umgebung '{domain.Environment}' existiert nicht.");
        }
        else if (envKey != null)
        {
            domain.Environment = envKey;
        }

        if (!string.IsNullOrWhiteSpace(domain.Name))
        {
            var existing = _configService.GetDomain(domain.Name);
            var renamedOrNew = model.OriginalName == null
                || !model.OriginalName.Equals(domain.Name, StringComparison.OrdinalIgnoreCase);
            if (existing != null && renamedOrNew)
            {
                ModelState.AddModelError(nameof(model.Name), $"Domain '{domain.Name}' existiert bereits.");
            }
        }

        if (envKey != null)
        {
            var envServers = _configService.GetServerNamesInEnvironment(envKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outside = domain.Servers.Where(s => !envServers.Contains(s)).ToList();
            if (outside.Count > 0)
            {
                ModelState.AddModelError(nameof(model.Servers),
                    $"Nicht in Umgebung '{envKey}': {string.Join(", ", outside)}. Server zuerst unter „Umgebungen“ zuordnen oder abwählen.");
            }

            // Keep the configured casing of server names.
            domain.Servers = domain.Servers
                .Select(s => config.Servers.FirstOrDefault(x => x.Name.Equals(s, StringComparison.OrdinalIgnoreCase))?.Name ?? s)
                .ToList();

            var convention = DomainNamingConvention.Evaluate(domain, config);
            if (convention.ShowWarning)
            {
                ModelState.AddModelError(nameof(model.AllowNameMismatch),
                    $"Der Name '{domain.Name}' deutet nach der Namenskonvention auf die Umgebung '{convention.SuggestedEnvironment}' hin, " +
                    $"gewählt ist '{envKey}'. Bitte Umgebung prüfen oder „Ausnahme zulassen“ ankreuzen.");
            }
        }

        return domain;
    }

    private DomainViewModel Populate(DomainViewModel model)
    {
        var config = _configService.Config;
        model.Environments = OrderedEnvironments();
        model.ServersByEnvironment = model.Environments.ToDictionary(e => e, e => _configService.GetServerNamesInEnvironment(e));
        model.ServerDescriptions = config.Servers
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);
        model.HealthCheckNames = _configService.GetHealthCheckNames();
        model.VServerNames = _configService.GetNetScalerVServerNames();
        model.SuffixMap = DomainNamingConvention.GetEffectiveSuffixMap(config.DomainSuffixMap);
        return model;
    }

    private List<string> OrderedEnvironments() =>
        _configService.GetEnvironmentNames()
            .OrderBy(EnvironmentOrder)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static int EnvironmentOrder(string env) => env.ToLowerInvariant() switch
    {
        "production" => 0,
        "ref" => 1,
        "development" => 2,
        _ => 3
    };

    private string? EnvironmentKey(string? env) =>
        string.IsNullOrWhiteSpace(env)
            ? null
            : _configService.GetEnvironmentNames().FirstOrDefault(k => k.Equals(env.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DomainConventionView ToView(DomainConventionResult r) =>
        new(r.SuggestedEnvironment, r.IsConsistent, r.ShowWarning);

    private IActionResult NoConfig()
    {
        TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
        return RedirectToAction("Index", "Home");
    }
}
