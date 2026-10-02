using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class HomeController : Controller
{
    private readonly ConfigurationService _configService;

    public HomeController(ConfigurationService configService)
    {
        _configService = configService;
    }

    public IActionResult Index()
    {
        ViewBag.IsLoaded = _configService.IsLoaded;
        ViewBag.HasChanges = _configService.HasChanges;
        ViewBag.LoadError = _configService.LoadError;
        ViewBag.ServerCount = _configService.IsLoaded ? _configService.GetServers().Count : 0;
        ViewBag.EnvironmentCount = _configService.IsLoaded ? _configService.GetEnvironmentNames().Count : 0;
        ViewBag.ActiveServerCount = _configService.IsLoaded
            ? _configService.GetServers().Count(s => s.Active)
            : 0;

        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
