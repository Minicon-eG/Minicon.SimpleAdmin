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

	/// <summary>
	/// Returns the full status page immediately with a skeleton UI.
	/// Status data is loaded asynchronously via GetAllServersJson() on the client.
	/// </summary>
	public IActionResult Index()
	{
		return View();
	}

	/// <summary>
	/// JSON endpoint for asynchronous status loading.
	/// Uses the StatusReaderService cache (10s TTL) so repeated calls are fast.
	/// </summary>
	[HttpGet]
	public async Task<IActionResult> GetAllServersJson([FromServices] StatusReaderService statusReader)
	{
		// Note: using the injected service directly (request-level DI scope)
		// because the action-scoped instance may differ from the singleton used by the page.
		var servers = await statusReader.GetAllServersAsync();
		return Json(servers);
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
