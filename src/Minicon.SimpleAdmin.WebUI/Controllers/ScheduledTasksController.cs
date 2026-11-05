using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

/// <summary>
/// Scheduled task page: task × server matrix per environment with single-active findings, control actions
/// (via <c>/api/scheduledtasks</c>) and the audit log. Filters (environment, text, only problems) are applied
/// client-side and kept in the query string, like the Problems page.
/// </summary>
public class ScheduledTasksController : Controller
{
    private readonly StatusReaderService _statusReader;
    private readonly ConfigurationService _configService;
    private readonly IScheduledTaskControl _control;

    public ScheduledTasksController(
        StatusReaderService statusReader,
        ConfigurationService configService,
        IScheduledTaskControl control)
    {
        _statusReader = statusReader;
        _configService = configService;
        _control = control;
    }

    public async Task<IActionResult> Index()
    {
        var featureEnabled = _configService.IsLoaded && _configService.GetFeatures().ScheduledTasks.Enabled;
        var groups = featureEnabled
            ? await _statusReader.GetScheduledTaskGroupsAsync()
            : Array.Empty<Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator.TaskGroup>();
        var workerUpdates = featureEnabled
            ? (await _statusReader.GetAllServersAsync())
                .GroupBy(s => s.ServerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(s => s.ScheduledTasks?.LastChecked ?? s.LastCheck), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var model = ScheduledTasksPageViewModel.Build(
            featureEnabled,
            groups,
            workerUpdates,
            _control.IsEnabled,
            _control.DisabledReason,
            _control.GetAuditLog(100));

        return View(model);
    }
}
