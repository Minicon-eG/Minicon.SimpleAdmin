using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class ProblemsController : Controller
{
    private readonly StatusReaderService _statusReader;

    public ProblemsController(StatusReaderService statusReader)
    {
        _statusReader = statusReader;
    }

    public async Task<IActionResult> Index()
    {
        var problems = await _statusReader.GetAllProblemsAsync();
        return View(problems);
    }
}
