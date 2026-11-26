using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services.Backup;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

[Route("Settings/Backup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SettingsBackupController(SettingsBackupService backups, SettingsBackupOptions options) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(backups.GetPage());

    [HttpPost("Download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Download()
    {
        try
        {
            var download = await backups.CreateDownloadAsync(CurrentUser);
            return File(download.Content, "application/zip", download.FileName);
        }
        catch (Exception ex)
        {
            TempData["Error"] = "Die Sicherung konnte nicht erstellt werden: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("AutomaticDownload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AutomaticDownload(string fileName)
    {
        try
        {
            var download = await backups.CreateAutomaticDownloadAsync(fileName, CurrentUser);
            return File(download.Content, "application/zip", download.FileName);
        }
        catch (Exception ex)
        {
            TempData["Error"] = "Die automatische Sicherung konnte nicht heruntergeladen werden: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Preview")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(SettingsBackupArchive.MaxUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = SettingsBackupArchive.MaxUploadBytes + 1024 * 1024)]
    public async Task<IActionResult> Preview(IFormFile? file)
    {
        if (!options.RestoreEnabled) return StatusCode(StatusCodes.Status403Forbidden, options.DisabledReason);
        if (file == null)
        {
            TempData["Error"] = "Bitte eine Sicherungsdatei auswählen.";
            return View("Index", backups.GetPage());
        }
        try
        {
            await using var input = file.OpenReadStream();
            var preview = await backups.PreviewUploadAsync(input, file.Length, file.FileName, CurrentUser);
            return View("Index", backups.GetPage(preview));
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
            return View("Index", backups.GetPage());
        }
    }

    [HttpPost("AutomaticPreview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AutomaticPreview(string fileName)
    {
        if (!options.RestoreEnabled) return StatusCode(StatusCodes.Status403Forbidden, options.DisabledReason);
        try
        {
            var preview = await backups.PreviewAutomaticAsync(fileName, CurrentUser);
            return View("Index", backups.GetPage(preview));
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
            return View("Index", backups.GetPage());
        }
    }

    [HttpPost("Restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(string token, bool removeUnlistedScripts = false)
    {
        if (!options.RestoreEnabled) return StatusCode(StatusCodes.Status403Forbidden, options.DisabledReason);
        var result = await backups.RestoreAsync(token, CurrentUser, removeUnlistedScripts);
        return View("Index", backups.GetPage(result: result));
    }

    private string CurrentUser => User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
        ? User.Identity.Name!
        : "anonymous";
}
