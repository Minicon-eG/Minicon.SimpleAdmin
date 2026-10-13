using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// Dry run for the file/log monitoring setup assistant (Servers → Edit). Takes the same row values
/// as the form (converted with the same view-model mapping as a save) and reports what the worker
/// would find right now. The file system is read by the WebUI process — the result also names the
/// machine/account, because the worker may run elsewhere with different rights.
/// Reading files is refused while authentication is disabled; testing pasted sample lines always works.
/// </summary>
[ApiController]
[Route("api/filemonitoringtest")]
public sealed class FileMonitoringTestController(
    FileMonitoringPreviewService preview,
    ConfigurationService configService,
    IConfiguration configuration) : ControllerBase
{
    public sealed record DirectoryRequest(WatchedDirectoryRowViewModel? Directory);
    public sealed record LogScanRequest(LogScanRowViewModel? LogScan, string? SampleText);

    private const string FileAccessDisabled =
        "Der Dateizugriff des Assistenten ist ohne Anmeldung (Authentication:Enabled=false) gesperrt. " +
        "Beispielzeilen einfügen funktioniert trotzdem.";

    [HttpPost("directory")]
    public async Task<IActionResult> TestDirectory([FromBody] DirectoryRequest request, CancellationToken cancellationToken)
    {
        if (!SimpleAdminWebUIExtensions.IsAuthenticationEnabled(configuration))
            return Ok(new { success = false, error = FileAccessDisabled });

        var dir = (request.Directory ?? new WatchedDirectoryRowViewModel()).ToWatchedDirectory();
        var result = await preview.PreviewDirectoryAsync(dir, Defaults(), cancellationToken);
        return Ok(new { success = result.Error == null, error = result.Error, checkedBy = CheckedBy(), result });
    }

    [HttpPost("logscan")]
    public async Task<IActionResult> TestLogScan([FromBody] LogScanRequest request, CancellationToken cancellationToken)
    {
        var usesFiles = string.IsNullOrWhiteSpace(request.SampleText);
        if (usesFiles && !SimpleAdminWebUIExtensions.IsAuthenticationEnabled(configuration))
            return Ok(new { success = false, error = FileAccessDisabled });

        var scan = (request.LogScan ?? new LogScanRowViewModel()).ToLogScanConfig();
        var result = await preview.PreviewLogScanAsync(scan, request.SampleText, cancellationToken);
        return Ok(new
        {
            success = result.Error == null,
            error = result.Error,
            checkedBy = usesFiles ? CheckedBy() : null,
            result
        });
    }

    private FileMonitoringDefaults Defaults()
        => configService.GetFeatures().FileMonitoring?.Defaults ?? new FileMonitoringDefaults();

    private static string CheckedBy() => $"{Environment.MachineName} als {Environment.UserDomainName}\\{Environment.UserName}";
}
