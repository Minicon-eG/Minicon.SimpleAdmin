using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

public class ServersController : Controller
{
    private readonly ConfigurationService _configService;
    private readonly ILogger<ServersController> _logger;

    public ServersController(ConfigurationService configService, ILogger<ServersController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public IActionResult Index()
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var servers = _configService.GetServers();
        return View(servers);
    }

    public IActionResult Create()
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var viewModel = new ServerViewModel
        {
            Active = true,
            ServiceTypes = new List<ServiceTypeViewModel>()
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServerViewModel model)
    {
        ValidateAppPoolChecks(model);
        ValidateServiceChecks(model);
        ValidateSqlQueryChecks(model);
        ValidateEmailProbeChecks(model);
        ValidateEmailDeliveryChecks(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var server = model.ToServer();
            _configService.EncryptServerConnectionStrings(server);
            await _configService.MutateAndSaveAsync(() => _configService.AddServer(server));
            TempData["Success"] = $"Server '{server.Name}' wurde erfolgreich erstellt.";
            return RedirectToAction(nameof(Edit), new { id = server.Name });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    public IActionResult Edit(string id)
    {
        if (!_configService.IsLoaded)
        {
            TempData["Error"] = "Keine Konfiguration geladen. Bitte prüfen Sie die Einstellung 'ConfigPath' in appsettings.json.";
            return RedirectToAction("Index", "Home");
        }

        var server = _configService.GetServer(id);
        if (server == null)
        {
            TempData["Error"] = $"Server '{id}' nicht gefunden.";
            return RedirectToAction(nameof(Index));
        }

        // Debug: Log raw server data before conversion
        System.Diagnostics.Debug.WriteLine($"=== Loading Server {id} ===");
        foreach (var st in server.ServiceTypes ?? new List<Minicon.SimpleAdmin.Models.Config.ServiceType>())
        {
            System.Diagnostics.Debug.WriteLine($"  ServiceType: {st.Type}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.Enabled: {st.NetScaler?.Enabled}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.AdminPath: {st.NetScaler?.AdminPath}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.HealthChecks: {st.NetScaler?.HealthChecks?.Count ?? 0}");
            if (st.NetScaler?.HealthChecks != null)
            {
                foreach (var hc in st.NetScaler.HealthChecks)
                {
                    System.Diagnostics.Debug.WriteLine($"      HealthCheck: {hc.Name} - {hc.Method} {hc.Path}");
                }
            }
        }

        var viewModel = ServerViewModel.FromServer(server);

        // Decrypt connection strings so the edit form shows the real (masked) values
        foreach (var check in viewModel.Checks?.SqlQueryChecks?.Checks ?? [])
            if (!string.IsNullOrEmpty(check.ConnectionString))
                check.ConnectionString = _configService.DecryptConnectionString(check.ConnectionString);

        foreach (var probe in viewModel.Checks?.EmailProbes?.Probes ?? [])
            if (!string.IsNullOrEmpty(probe.SmtpPassword))
                probe.SmtpPassword = _configService.DecryptConnectionString(probe.SmtpPassword);

        if (viewModel.Checks?.EmailDelivery != null && !string.IsNullOrEmpty(viewModel.Checks.EmailDelivery.ImapPassword))
            viewModel.Checks.EmailDelivery.ImapPassword = _configService.DecryptConnectionString(viewModel.Checks.EmailDelivery.ImapPassword);

        // Debug: Log ViewModel after conversion
        System.Diagnostics.Debug.WriteLine($"=== ViewModel for {id} ===");
        foreach (var st in viewModel.ServiceTypes ?? new List<ViewModels.ServiceTypeViewModel>())
        {
            System.Diagnostics.Debug.WriteLine($"  ServiceType: {st.Type}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.Enabled: {st.NetScaler?.Enabled}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.AdminPath: {st.NetScaler?.AdminPath}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.HealthChecks: {st.NetScaler?.HealthChecks?.Count ?? 0}");
        }

        ViewBag.OriginalName = id;

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, ServerViewModel model)
    {
        // Debug: Log what we received
        System.Diagnostics.Debug.WriteLine($"=== Edit Server {id} ===");
        System.Diagnostics.Debug.WriteLine($"ServiceTypes count: {model.ServiceTypes?.Count ?? 0}");
        foreach (var st in model.ServiceTypes ?? new List<ServiceTypeViewModel>())
        {
            System.Diagnostics.Debug.WriteLine($"  ServiceType: {st.Type}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.Enabled: {st.NetScaler?.Enabled}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.AdminPath: {st.NetScaler?.AdminPath}");
            System.Diagnostics.Debug.WriteLine($"    NetScaler.HealthChecks: {st.NetScaler?.HealthChecks?.Count ?? 0}");
            foreach (var hc in st.NetScaler?.HealthChecks ?? new List<HealthCheckViewModel>())
            {
                System.Diagnostics.Debug.WriteLine($"      HealthCheck: {hc.Name} - {hc.Method} {hc.Path}");
            }
        }

        foreach (var check in model.Checks?.SqlQueryChecks?.Checks ?? [])
            foreach (var query in check.Queries ?? [])
                _logger.LogInformation("SQL check '{Check}' query '{Query}': {Count} column assertion(s) bound",
                    check.Name, query.Name, query.ColumnAssertions?.Count ?? 0);

        _logger.LogInformation("EmailProbes binding: Enabled={Enabled}, ProbeCount={Count}",
            model.Checks?.EmailProbes?.Enabled,
            model.Checks?.EmailProbes?.Probes?.Count ?? 0);
        if (model.Checks?.EmailProbes?.Probes?.Count > 0)
        {
            var p = model.Checks.EmailProbes.Probes[0];
            _logger.LogInformation("Probe[0]: Name={Name}, Host={Host}, From={From}, To={To}, Subject={Subject}",
                p.Name, p.SmtpHost, p.SmtpFrom, p.SmtpTo, p.Subject);
        }
        _logger.LogInformation("EmailDelivery binding: Enabled={Enabled}, CheckCount={Count}",
            model.Checks?.EmailDelivery?.Enabled,
            model.Checks?.EmailDelivery?.Checks?.Count ?? 0);

        ValidateAppPoolChecks(model);
        ValidateServiceChecks(model);
        ValidateSqlQueryChecks(model);
        ValidateEmailProbeChecks(model);
        ValidateEmailDeliveryChecks(model);

        if (!ModelState.IsValid)
        {
            foreach (var kvp in ModelState.Where(kvp => kvp.Value?.Errors.Count > 0))
                foreach (var error in kvp.Value!.Errors)
                    _logger.LogWarning("ModelState error [{Key}]: {Error}", kvp.Key, error.ErrorMessage);
            ViewBag.OriginalName = id;
            return View(model);
        }

        try
        {
            var originalChecks = _configService.GetServer(id)?.Checks;
            var server = model.ToServer(originalChecks);
            _configService.EncryptServerConnectionStrings(server);
            await _configService.MutateAndSaveAsync(() => _configService.UpdateServer(id, server));
            TempData["Success"] = $"Server '{server.Name}' wurde erfolgreich aktualisiert und gespeichert.";

            var updated = _configService.GetServer(server.Name);
            var viewModel = updated != null ? ServerViewModel.FromServer(updated) : model;
            foreach (var check in viewModel.Checks?.SqlQueryChecks?.Checks ?? [])
                if (!string.IsNullOrEmpty(check.ConnectionString))
                    check.ConnectionString = _configService.DecryptConnectionString(check.ConnectionString);

            foreach (var probe in viewModel.Checks?.EmailProbes?.Probes ?? [])
                if (!string.IsNullOrEmpty(probe.SmtpPassword))
                    probe.SmtpPassword = _configService.DecryptConnectionString(probe.SmtpPassword);

            if (viewModel.Checks?.EmailDelivery != null && !string.IsNullOrEmpty(viewModel.Checks.EmailDelivery.ImapPassword))
                viewModel.Checks.EmailDelivery.ImapPassword = _configService.DecryptConnectionString(viewModel.Checks.EmailDelivery.ImapPassword);

            ViewBag.OriginalName = server.Name;
            return View(viewModel);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            ViewBag.OriginalName = id;
            return View(model);
        }
    }

    private void ValidateAppPoolChecks(ServerViewModel model)
    {
        var appPools = model.Checks?.AppPools;
        if (appPools is not { Enabled: true }) return;
        if (appPools.DiscoverAll) return;

        if (!appPools.ManualPools.Any(p => !string.IsNullOrWhiteSpace(p)))
            ModelState.AddModelError("",
                "AppPool-Überwachung (Manuell): Mindestens ein AppPool muss konfiguriert sein.");
    }

    private void ValidateServiceChecks(ServerViewModel model)
    {
        if (!model.Checks.ServicesEnabled) return;

        if (!model.Checks.Services.Any(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            ModelState.AddModelError("",
                "Windows Service-Überwachung: Mindestens ein Windows-Dienst muss konfiguriert sein.");
            return;
        }

        foreach (var svc in model.Checks.Services.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            if (!svc.MonitorAutomatic && !svc.MonitorManual)
                ModelState.AddModelError("",
                    $"Dienst '{svc.Name}': Mindestens eine Startart (Automatisch oder Manuell) muss ausgewählt sein.");
        }
    }

    private void ValidateSqlQueryChecks(ServerViewModel model)
    {
        if (!model.Checks.SqlQueryChecks.Enabled) return;

        foreach (var check in model.Checks.SqlQueryChecks.Checks
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.ConnectionString)))
        {
            if (!check.Queries.Any(q => !string.IsNullOrWhiteSpace(q.Sql)))
                ModelState.AddModelError("",
                    $"Check-Gruppe '{check.Name}': Mindestens eine Abfrage mit SQL muss konfiguriert sein.");
        }
    }

    private void ValidateEmailProbeChecks(ServerViewModel model)
    {
        if (model.Checks?.EmailProbes?.Enabled != true) return;

        var validProbes = model.Checks.EmailProbes.Probes
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        if (validProbes.Count == 0)
        {
            ModelState.AddModelError("",
                "E-Mail-Probes aktiviert: Mindestens eine Probe muss konfiguriert sein.");
            return;
        }

        if (validProbes.Count > 1)
        {
            ModelState.AddModelError("",
                "E-Mail-Probes: Es darf nur eine Probe pro Server konfiguriert sein.");
            return;
        }

        foreach (var probe in validProbes)
        {
            if (string.IsNullOrWhiteSpace(probe.SmtpHost))
                ModelState.AddModelError("",
                    $"E-Mail-Probe '{probe.Name}': SMTP-Host darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(probe.SmtpFrom))
                ModelState.AddModelError("",
                    $"E-Mail-Probe '{probe.Name}': Absender (From) darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(probe.SmtpTo))
                ModelState.AddModelError("",
                    $"E-Mail-Probe '{probe.Name}': Empfänger (To) darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(probe.Subject))
                ModelState.AddModelError("",
                    $"E-Mail-Probe '{probe.Name}': Betreff darf nicht leer sein.");
        }
    }

    private void ValidateEmailDeliveryChecks(ServerViewModel model)
    {
        if (model.Checks?.EmailDelivery?.Enabled != true) return;

        if (string.IsNullOrWhiteSpace(model.Checks.EmailDelivery.ImapHost))
            ModelState.AddModelError("",
                "E-Mail-Zustellung aktiviert: IMAP-Host darf nicht leer sein.");

        var validChecks = model.Checks.EmailDelivery.Checks
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.SubjectFilter))
            .ToList();

        if (validChecks.Count == 0)
            ModelState.AddModelError("",
                "E-Mail-Zustellung aktiviert: Mindestens ein Zustellungs-Check (mit Name und Betreff-Filter) muss konfiguriert sein.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _configService.MutateAndSaveAsync(() => _configService.DeleteServer(id));
            TempData["Success"] = $"Server '{id}' wurde erfolgreich gelöscht.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(string id)
    {
        var server = _configService.GetServer(id);
        if (server != null)
        {
            try
            {
                await _configService.MutateAndSaveAsync(() => server.Active = !server.Active);
                TempData["Success"] = $"Server '{id}' ist jetzt {(server.Active ? "aktiv" : "inaktiv")}.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Fehler beim Speichern: {ex.Message}";
            }
        }

        return RedirectToAction(nameof(Index));
    }

    #region ServiceType Management

    [HttpPost]
    public IActionResult AddServiceType(string serverName)
    {
        return PartialView("_ServiceTypePartial", new ServiceTypeViewModel
        {
            Index = int.Parse(Request.Form["index"].ToString()),
            Type = "",
            Prtg = new PrtgConfigViewModel 
            { 
                Enabled = true,
                AdminPath = "" // Will be set by JavaScript when service type is selected
            },
            NetScaler = new NetScalerConfigViewModel 
            { 
                Enabled = false,
                AdminPath = "" // Will be set by JavaScript when service type is selected
            }
        });
    }

    [HttpPost]
    public IActionResult AddPrtgCheck(int serviceIndex, int checkIndex)
    {
        return PartialView("_PrtgCheckPartial", new PrtgCheckViewModel
        {
            ServiceIndex = serviceIndex,
            CheckIndex = checkIndex,
            Metric = "cpu",
            DisplayName = "CPU Usage",
            Threshold = new ThresholdViewModel
            {
                Warning = 70,
                Critical = 85,
                Operator = ">"
            }
        });
    }

    [HttpPost]
    public IActionResult AddHealthCheck(int serviceIndex, int checkIndex)
    {
        return PartialView("_HealthCheckPartial", new HealthCheckViewModel
        {
            ServiceIndex = serviceIndex,
            CheckIndex = checkIndex,
            Name = "",
            Method = "GET",
            Path = "/health",
            IntervalSeconds = 30,
            TimeoutMs = 5000
        });
    }

    #endregion

    #region SQL Query Check Management

    [HttpPost]
    public IActionResult AddSqlQueryCheck()
    {
        var checkIndex = int.TryParse(Request.Form["checkIndex"].ToString(), out var ci) ? ci : 0;
        return PartialView("_SqlQueryCheckPartial", new SqlQueryCheckViewModel
        {
            Index = checkIndex,
            Name = "",
            Description = "",
            ConnectionString = "",
            Queries = new List<SqlQueryViewModel>()
        });
    }

    [HttpPost]
    public IActionResult AddSqlQuery()
    {
        var checkIndex = int.TryParse(Request.Form["checkIndex"].ToString(), out var ci) ? ci : 0;
        var queryIndex = int.TryParse(Request.Form["queryIndex"].ToString(), out var qi) ? qi : 0;
        return PartialView("_SqlQueryPartial", new SqlQueryViewModel
        {
            Index = queryIndex,
            CheckIndex = checkIndex,
            Name = "",
            Sql = "",
            HasRowCount = false,
            RowCountExpected = 0,
            RowCountOperator = "==",
            ColumnAssertions = new List<SqlColumnAssertionViewModel>()
        });
    }

    [HttpPost]
    public IActionResult AddSqlColumnAssertion()
    {
        var checkIndex = int.TryParse(Request.Form["checkIndex"].ToString(), out var ci) ? ci : 0;
        var queryIndex = int.TryParse(Request.Form["queryIndex"].ToString(), out var qi) ? qi : 0;
        var assertionIndex = int.TryParse(Request.Form["assertionIndex"].ToString(), out var ai) ? ai : 0;
        return PartialView("_SqlColumnAssertionPartial", new SqlColumnAssertionViewModel
        {
            Index = assertionIndex,
            Name = "",
            Column = "",
            ExpectedValue = "",
            Operator = "==",
            ValueType = "String"
        });
    }

    #endregion

    #region Email Monitoring Management

    [HttpPost]
    public IActionResult AddEmailProbe()
    {
        var probeIndex = int.TryParse(Request.Form["probeIndex"].ToString(), out var pi) ? pi : 0;
        return PartialView("_EmailProbeEditorPartial", new EmailProbeEditorViewModel
        {
            Index = probeIndex,
            SmtpPort = 25,
            SendIntervalMinutes = 15
        });
    }

    [HttpPost]
    public IActionResult AddEmailDeliveryCheck()
    {
        var checkIndex = int.TryParse(Request.Form["checkIndex"].ToString(), out var ci) ? ci : 0;
        return PartialView("_EmailDeliveryCheckEditorPartial", new EmailDeliveryCheckEditorViewModel
        {
            Index = checkIndex,
            MaxAgeWarningMinutes = 20,
            MaxAgeCriticalMinutes = 60,
            DeleteAfterCheck = true
        });
    }

    #endregion
}
