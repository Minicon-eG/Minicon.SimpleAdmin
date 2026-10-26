using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class DocumentationController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>"Neuigkeiten": release notes of the installed version and its predecessors.</summary>
    public IActionResult Changelog()
    {
        // Resolved here instead of via the constructor: ChangelogProvider is internal.
        var provider = HttpContext.RequestServices.GetRequiredService<ChangelogProvider>();
        return View(provider.Releases);
    }
}
