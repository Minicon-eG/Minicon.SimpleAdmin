using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

/// <summary>
/// REST endpoints of the script runner: start a run, poll its output, cancel it and browse
/// the WebUI host's file system for the file/folder chooser. All POSTs require a JSON body
/// (<c>[Consumes("application/json")]</c>) so they cannot be triggered by a cross-site form
/// post riding on the user's Windows credentials.
/// </summary>
[ApiController]
[Route("api/scripts")]
[ScriptRunnerGate]
public class ScriptsApiController : ControllerBase
{
    private const int MaxBrowseEntries = 2000;

    private readonly ScriptDefinitionStore _store;
    private readonly ScriptRunService _runs;
    private readonly ScriptPathPolicy _pathPolicy;

    public ScriptsApiController(ScriptDefinitionStore store, ScriptRunService runs, ScriptPathPolicy pathPolicy)
    {
        _store = store;
        _runs = runs;
        _pathPolicy = pathPolicy;
    }

    public sealed class StartRunRequest
    {
        public Dictionary<string, List<string>> Values { get; set; } = new();
    }

    [HttpPost("{id}/runs")]
    [Consumes("application/json")]
    public IActionResult Start(string id, [FromBody] StartRunRequest request)
    {
        var definition = _store.Get(id);
        if (definition == null) return NotFound(new { message = "Skript nicht gefunden." });

        try
        {
            var user = User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
            var run = _runs.Start(definition, request.Values ?? new(), user);
            return Ok(new
            {
                runId = run.Id,
                status = run.Status.ToString(),
                outputUrl = Url.Action("Output", "Scripts", new { id = run.Id })
            });
        }
        catch (ScriptRunException ex)
        {
            return BadRequest(new { message = "Das Skript wurde nicht gestartet.", errors = ex.Errors });
        }
    }

    [HttpGet("runs/{runId}")]
    public IActionResult Output(string runId, [FromQuery] int from = 0)
    {
        var run = _runs.GetRun(runId);
        if (run == null) return NotFound(new { message = "Lauf nicht gefunden." });
        var (lines, next, skipped) = _runs.GetOutput(runId, Math.Max(0, from));
        return Ok(new
        {
            status = run.Status.ToString(),
            statusText = ScriptRunService.StatusText(run.Status),
            running = run.IsRunning,
            exitCode = run.ExitCode,
            error = run.Error,
            finishedAt = run.FinishedAt,
            durationSeconds = (int)run.Duration.TotalSeconds,
            next,
            skipped,
            lines = lines.Select(l => new { t = l.Timestamp, e = l.IsError, x = l.Text })
        });
    }

    [HttpPost("runs/{runId}/cancel")]
    [Consumes("application/json")]
    public IActionResult Cancel(string runId, [FromBody] object? body)
    {
        var user = User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        return _runs.Cancel(runId, user)
            ? Ok(new { message = "Abbruch angefordert." })
            : BadRequest(new { message = "Der Lauf ist nicht (mehr) aktiv." });
    }

    /// <summary>
    /// Lists a directory of the WebUI host for the file/folder chooser.
    /// <paramref name="mode"/>: <c>file</c> (directories + files) or <c>folder</c> (directories only);
    /// <paramref name="extension"/> optionally filters files (e.g. <c>.ps1</c>).
    /// </summary>
    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path, [FromQuery] string mode = "file", [FromQuery] string? extension = null)
    {
        var roots = _pathPolicy.GetRoots();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Ok(new
            {
                path = (string?)null,
                parent = (string?)null,
                roots,
                entries = roots.Select(r => new { name = r, fullPath = r, isDirectory = true })
            });
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return BadRequest(new { message = "Ungültiger Pfad." });
        }
        if (!Directory.Exists(full) && System.IO.File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;
        if (!Directory.Exists(full))
        {
            // Walk up to the nearest existing directory (e.g. default "C:\Temp\FileCopy_Log.csv" whose folder exists).
            var probe = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe)) return NotFound(new { message = $"Verzeichnis nicht gefunden: {full}", roots });
            full = probe;
        }
        if (!_pathPolicy.CanBrowse(full))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Dieses Verzeichnis ist für die Auswahl nicht freigegeben.", roots });

        try
        {
            var dir = new DirectoryInfo(full);
            var entries = new List<object>();
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) continue;
                entries.Add(new { name = d.Name, fullPath = d.FullName, isDirectory = true });
                if (entries.Count >= MaxBrowseEntries) break;
            }
            if (!mode.Equals("folder", StringComparison.OrdinalIgnoreCase) && entries.Count < MaxBrowseEntries)
            {
                foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(extension) && !f.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(new { name = f.Name, fullPath = f.FullName, isDirectory = false, size = f.Length, modified = f.LastWriteTime });
                    if (entries.Count >= MaxBrowseEntries) break;
                }
            }

            var parent = dir.Parent?.FullName;
            if (parent != null && !_pathPolicy.CanBrowse(parent)) parent = null;
            return Ok(new { path = dir.FullName, parent, roots, entries, truncated = entries.Count >= MaxBrowseEntries });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = $"Kein Zugriff auf {full} (Konto der WebUI).", roots });
        }
        catch (IOException ex)
        {
            return BadRequest(new { message = ex.Message, roots });
        }
    }
}
