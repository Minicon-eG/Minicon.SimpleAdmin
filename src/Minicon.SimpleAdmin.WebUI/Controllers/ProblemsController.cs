using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

/// <summary>
/// The problems list of all servers. Also the entry point for the BizTalk deactivation actions:
/// whether they are offered depends on <c>features.bizTalk.actionsEnabled</c>.
/// </summary>
public class ProblemsController : Controller
{
    private readonly StatusReaderService _statusReader;
    private readonly ConfigurationService _configService;

    public ProblemsController(StatusReaderService statusReader, ConfigurationService configService)
    {
        _statusReader = statusReader;
        _configService = configService;
    }

    public async Task<IActionResult> Index()
    {
        var problems = await _statusReader.GetAllProblemsAsync();
        ViewBag.BizTalkActionsEnabled =
            _configService.IsLoaded && _configService.GetFeatures().BizTalk.ActionsEnabled;

        return View(problems);
    }
}
