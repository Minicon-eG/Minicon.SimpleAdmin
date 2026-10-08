using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class StatusController : Controller
{
    private readonly StatusReaderService _statusReader;

    public StatusController(StatusReaderService statusReader)
    {
        _statusReader = statusReader;
    }

    public async Task<IActionResult> Index()
    {
        var servers = await _statusReader.GetAllServersAsync();
        if (servers.Count == 0)
        {
            ViewData["Diagnostics"] = _statusReader.GetDiagnostics();
        }
        return View(servers);
    }

    public async Task<IActionResult> Details(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return RedirectToAction(nameof(Index));
        }

        var server = await _statusReader.GetServerAsync(id);
        if (server == null)
        {
            TempData["Error"] = $"Server '{id}' nicht gefunden.";
            return RedirectToAction(nameof(Index));
        }

        return View(server);
    }
}
