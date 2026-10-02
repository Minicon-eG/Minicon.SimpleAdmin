using Microsoft.AspNetCore.Mvc;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class DocumentationController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
