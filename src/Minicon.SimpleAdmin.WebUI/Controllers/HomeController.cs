using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class HomeController : Controller
{
    private readonly ConfigurationService _configService;
    private readonly StatusReaderService _statusReader;
    private readonly ILogger<HomeController> _logger;

    public HomeController(ConfigurationService configService, StatusReaderService statusReader, ILogger<HomeController> logger)
    {
        _configService = configService;
        _statusReader = statusReader;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        ViewBag.IsLoaded = _configService.IsLoaded;
        ViewBag.HasChanges = _configService.HasChanges;
        ViewBag.LoadError = _configService.LoadError;
        ViewBag.ServerCount = _configService.IsLoaded ? _configService.GetServers().Count : 0;
        ViewBag.EnvironmentCount = _configService.IsLoaded ? _configService.GetEnvironmentNames().Count : 0;
        ViewBag.ActiveServerCount = _configService.IsLoaded
            ? _configService.GetServers().Count(s => s.Active)
            : 0;
        ViewBag.Monitoring = await LoadMonitoringAsync();

        return View();
    }

    // The dashboard must render even when the status sources are unreachable.
    private async Task<DashboardMonitoringView> LoadMonitoringAsync()
    {
        try
        {
            return DashboardMonitoringView.Build(await _statusReader.GetAllServersAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statusdaten für das Dashboard konnten nicht gelesen werden");
            return DashboardMonitoringView.Unavailable(ex.Message);
        }
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
