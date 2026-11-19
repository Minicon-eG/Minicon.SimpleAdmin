using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.BizTalk;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public sealed class BizTalkController(ConfigurationService config, BizTalkOptions options) : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Instances(string? server, string? application, string? error, bool missingApplication = false)
    {
        var servers = config.IsLoaded ? config.GetServers()
            .Where(s => BizTalkTargets.Resolve(config.Config, s.GetEffectiveId(), options) != null)
            .Select(s => new BizTalkServerChoice(s.GetEffectiveId(), s.GetEffectiveDisplayName())).ToArray() : [];
        // Only an absent reference may select the first target. Never silently replace a stale deep link.
        var serverSpecified = server != null || HttpContext?.Request.Query.ContainsKey("server") == true;
        var target = config.IsLoaded && serverSpecified ? BizTalkTargets.Resolve(config.Config, server, options) : null;
        var invalidReference = serverSpecified && target == null;
        var selectedServer = !serverSpecified ? servers.FirstOrDefault()?.Id : target?.ServerId;
        var reason = invalidReference
            ? "Der angeforderte Server ist kein aktives, freigegebenes BizTalk-Ziel. Bitte öffnen Sie die Live-Liste erneut über die Serverdetails."
            : options.DisabledReason ?? (servers.Length == 0 ? "Keine aktiven, freigegebenen BizTalk-Ziele konfiguriert." : null);
        var enabled = options.ControlEnabled && servers.Length > 0 && !invalidReference;
        if (invalidReference) Response.StatusCode = 404;
        return View(new BizTalkInstancesViewModel(servers, selectedServer,
            application, error, missingApplication, enabled, reason));
    }
}
