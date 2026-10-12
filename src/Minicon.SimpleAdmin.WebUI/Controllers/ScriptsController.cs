using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Controllers;

/// <summary>
/// "Skripte": register PowerShell scripts (upload or server path), adjust the automatically
/// detected parameters, start runs and follow their output.
/// </summary>
[ScriptRunnerGate]
public class ScriptsController : Controller
{
    private const long MaxUploadBytes = 2 * 1024 * 1024;
    private static readonly Regex ValidParameterName = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    private readonly ScriptDefinitionStore _store;
    private readonly ScriptRunService _runs;
    private readonly ScriptPathPolicy _pathPolicy;
    private readonly ILogger<ScriptsController> _logger;

    public ScriptsController(ScriptDefinitionStore store, ScriptRunService runs, ScriptPathPolicy pathPolicy, ILogger<ScriptsController> logger)
    {
        _store = store;
        _runs = runs;
        _pathPolicy = pathPolicy;
        _logger = logger;
    }

    public IActionResult Index()
    {
        var model = new ScriptsIndexViewModel
        {
            Scripts = _store.GetAll().ToList(),
            RecentRuns = _runs.GetHistory(max: 30).ToList()
        };
        return View(model);
    }

    public IActionResult Create()
    {
        ViewData["UploadDirectory"] = _pathPolicy.UploadDirectory;
        ViewData["AllowedDirectories"] = _pathPolicy.AllowedDirectories;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFromPath(string path)
    {
        path = (path ?? string.Empty).Trim().Trim('"');
        var error = _pathPolicy.ValidateScriptPath(path);
        if (error == null && !System.IO.File.Exists(path)) error = $"Datei nicht gefunden: {path}";
        if (error != null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Create));
        }

        var definition = PowerShellScriptParser.CreateDefinition(Path.GetFullPath(path), await ReadScriptAsync(path));
        _store.Save(definition);
        TempData["Success"] = DetectedMessage(definition);
        return RedirectToAction(nameof(Edit), new { id = definition.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes + 64 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, bool overwrite)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Bitte eine .ps1-Datei auswählen.";
            return RedirectToAction(nameof(Create));
        }
        if (file.Length > MaxUploadBytes)
        {
            TempData["Error"] = "Die Datei ist zu groß (maximal 2 MB).";
            return RedirectToAction(nameof(Create));
        }

        var fileName = SanitizeFileName(file.FileName);
        if (!fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Nur PowerShell-Skripte (.ps1) können hochgeladen werden.";
            return RedirectToAction(nameof(Create));
        }

        Directory.CreateDirectory(_pathPolicy.UploadDirectory);
        var target = Path.Combine(_pathPolicy.UploadDirectory, fileName);
        var existing = _store.GetAll().FirstOrDefault(d => string.Equals(d.Path, target, StringComparison.OrdinalIgnoreCase));
        if (System.IO.File.Exists(target) && !overwrite)
        {
            TempData["Error"] = $"'{fileName}' existiert bereits. Zum Ersetzen \"Vorhandene Datei überschreiben\" anhaken.";
            return RedirectToAction(nameof(Create));
        }

        await using (var stream = System.IO.File.Create(target))
            await file.CopyToAsync(stream);
        _logger.LogInformation("Script uploaded to {Path} by {User}", target, CurrentUser);

        var content = await ReadScriptAsync(target);
        ScriptDefinition definition;
        if (existing != null)
        {
            // Same file uploaded again → update the parameters of the existing definition.
            var fresh = PowerShellScriptParser.CreateDefinition(target, content);
            existing.Parameters = PowerShellScriptParser.MergeParameters(existing.Parameters, fresh.Parameters);
            existing.ParametersReadAt = DateTime.UtcNow;
            definition = existing;
        }
        else
        {
            definition = PowerShellScriptParser.CreateDefinition(target, content);
        }
        _store.Save(definition);
        TempData["Success"] = DetectedMessage(definition);
        return RedirectToAction(nameof(Edit), new { id = definition.Id });
    }

    public async Task<IActionResult> Edit(string id)
    {
        var definition = _store.Get(id);
        if (definition == null) return NotFound();
        await FillSourceAsync(definition);
        return View(definition);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, ScriptDefinition model)
    {
        var existing = _store.Get(id);
        if (existing == null) return NotFound();

        model.Id = existing.Id;
        model.ParametersReadAt = existing.ParametersReadAt;
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Path = model.Path?.Trim().Trim('"') ?? string.Empty;
        model.Parameters = (model.Parameters ?? new List<ScriptParameterDefinition>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
        foreach (var p in model.Parameters)
        {
            p.Name = p.Name.Trim().TrimStart('$');
            if (string.IsNullOrWhiteSpace(p.DefaultValue)) p.DefaultValue = null;
            if (p.Type == ScriptParameterType.Switch && p.DefaultValue != null)
                p.DefaultValue = p.DefaultValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : null;
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(model.Name)) errors.Add("Bitte einen Namen angeben.");
        var pathError = _pathPolicy.ValidateScriptPath(model.Path);
        if (pathError != null) errors.Add(pathError);
        if (model.TimeoutMinutes < 0) errors.Add("Das Zeitlimit darf nicht negativ sein.");
        foreach (var p in model.Parameters.Where(p => !ValidParameterName.IsMatch(p.Name)))
            errors.Add($"Ungültiger Parametername '{p.Name}'.");
        foreach (var dup in model.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Parameter '{dup.Key}' ist mehrfach definiert.");
        foreach (var p in model.Parameters.Where(p => p.Type == ScriptParameterType.Choice && p.Choices.Count == 0))
            errors.Add($"'{p.DisplayLabel}': Für eine Auswahlliste werden Auswahlwerte benötigt.");
        foreach (var p in model.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Pattern)))
        {
            try { _ = new Regex(p.Pattern!); }
            catch (ArgumentException) { errors.Add($"'{p.DisplayLabel}': Ungültiges Muster (Regex)."); }
        }
        foreach (var p in model.Parameters.Where(p => p.Fixed && p.Required && string.IsNullOrWhiteSpace(p.DefaultValue) && p.Type != ScriptParameterType.Switch))
            errors.Add($"'{p.DisplayLabel}': Ein fester Pflichtparameter braucht einen Standardwert.");

        if (errors.Count > 0)
        {
            TempData["Error"] = string.Join(" ", errors);
            await FillSourceAsync(model);
            return View(model);
        }

        _store.Save(model);
        TempData["Success"] = "Skript gespeichert.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reparse(string id)
    {
        var definition = _store.Get(id);
        if (definition == null) return NotFound();
        if (!System.IO.File.Exists(definition.Path))
        {
            TempData["Error"] = $"Datei nicht gefunden: {definition.Path}";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var fresh = PowerShellScriptParser.CreateDefinition(definition.Path, await ReadScriptAsync(definition.Path));
        var before = definition.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        definition.Parameters = PowerShellScriptParser.MergeParameters(definition.Parameters, fresh.Parameters);
        definition.ParametersReadAt = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(definition.Description)) definition.Description = fresh.Description;
        _store.Save(definition);

        var added = definition.Parameters.Count(p => !before.Contains(p.Name));
        var removed = before.Count(n => definition.Parameters.All(p => !p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        TempData["Success"] = $"Parameter neu eingelesen: {definition.Parameters.Count} Parameter ({added} neu, {removed} entfernt). Eigene Anpassungen wurden beibehalten.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(string id)
    {
        var definition = _store.Get(id);
        if (definition != null && _store.Delete(id))
            TempData["Success"] = $"'{definition.Name}' wurde entfernt (die Skriptdatei bleibt erhalten).";
        return RedirectToAction(nameof(Index));
    }

    public IActionResult Run(string id, string? fromRun)
    {
        var definition = _store.Get(id);
        if (definition == null) return NotFound();

        var model = new ScriptRunFormViewModel { Script = definition };
        if (!string.IsNullOrEmpty(fromRun))
            model.PreviousValues = _runs.GetRun(fromRun)?.InputValues;
        model.ActiveRun = _runs.GetHistory(definition.Id, 5).FirstOrDefault(r => r.IsRunning);
        model.RecentRuns = _runs.GetHistory(definition.Id, 10).ToList();
        model.ScriptExists = System.IO.File.Exists(definition.Path);
        return View(model);
    }

    public IActionResult Output(string id)
    {
        var run = _runs.GetRun(id);
        if (run == null) return NotFound();
        ViewData["ScriptExists"] = _store.Get(run.ScriptId) != null;
        return View(run);
    }

    /// <summary>Complete output of a run as a text file.</summary>
    public IActionResult Log(string id)
    {
        var run = _runs.GetRun(id);
        if (run == null) return NotFound();
        var sb = new StringBuilder();
        int from = 0;
        while (true)
        {
            var (lines, next, _) = _runs.GetOutput(id, from);
            foreach (var l in lines)
                sb.Append(l.Timestamp.ToLocalTime().ToString("HH:mm:ss")).Append(l.IsError ? " ! " : "   ").AppendLine(l.Text);
            if (lines.Count == 0 || next == from) break;
            from = next;
        }
        var name = $"{SanitizeFileName(run.ScriptName)}_{run.StartedAt.ToLocalTime():yyyyMMdd_HHmmss}.log";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/plain; charset=utf-8", name);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private string? CurrentUser => User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;

    private static async Task<string> ReadScriptAsync(string path)
    {
        // detectEncodingFromByteOrderMarks: Windows PowerShell scripts are often UTF-8 with BOM or UTF-16.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private async Task FillSourceAsync(ScriptDefinition definition)
    {
        try
        {
            if (System.IO.File.Exists(definition.Path) && new FileInfo(definition.Path).Length < 512 * 1024)
                ViewData["Source"] = await ReadScriptAsync(definition.Path);
            else if (!System.IO.File.Exists(definition.Path))
                ViewData["SourceError"] = $"Datei nicht gefunden: {definition.Path}";
        }
        catch (Exception ex)
        {
            ViewData["SourceError"] = $"Skript konnte nicht gelesen werden: {ex.Message}";
        }
    }

    private static string DetectedMessage(ScriptDefinition d)
    {
        var visible = d.Parameters.Count;
        if (visible == 0) return $"'{d.Name}' angelegt. Im Skript wurde kein param()-Block gefunden.";
        var summary = string.Join(", ", d.Parameters.GroupBy(p => p.Type).Select(g => $"{g.Count()}× {ScriptFormValues.TypeLabel(g.Key)}"));
        return $"'{d.Name}' angelegt: {visible} Parameter erkannt ({summary}). Bitte prüfen und speichern.";
    }

    internal static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace(':', '_').Replace('\\', '_').Replace('/', '_').Trim().TrimStart('.');
        return string.IsNullOrEmpty(name) ? "script.ps1" : name;
    }
}

public sealed class ScriptsIndexViewModel
{
    public List<ScriptDefinition> Scripts { get; set; } = new();
    public List<ScriptRun> RecentRuns { get; set; } = new();
}

public sealed class ScriptRunFormViewModel
{
    public ScriptDefinition Script { get; set; } = new();
    public Dictionary<string, List<string>>? PreviousValues { get; set; }
    public ScriptRun? ActiveRun { get; set; }
    public List<ScriptRun> RecentRuns { get; set; } = new();
    public bool ScriptExists { get; set; }
}
