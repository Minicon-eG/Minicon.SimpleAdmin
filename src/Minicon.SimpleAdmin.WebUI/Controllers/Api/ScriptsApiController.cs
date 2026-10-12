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
public sealed class ScriptsApiController : ControllerBase
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

    /// <summary>
    /// Output of a run. Without <paramref name="from"/>: the last lines (initial load, also for old runs
    /// read from the log file); with <paramref name="from"/>: new lines since that index (live polling).
    /// <c>resync</c> = the position is no longer in memory → the client reloads without <c>from</c>.
    /// </summary>
    [HttpGet("runs/{runId}")]
    public IActionResult Output(string runId, [FromQuery] int? from = null)
    {
        var run = _runs.GetRun(runId);
        if (run == null) return NotFound(new { message = "Lauf nicht gefunden." });
        var page = _runs.GetOutput(runId, from is null ? null : Math.Max(0, from.Value));
        return Ok(new
        {
            status = run.Status.ToString(),
            statusText = ScriptRunService.StatusText(run.Status),
            running = run.IsRunning,
            exitCode = run.ExitCode,
            error = run.Error,
            finishedAt = run.FinishedAt,
            durationSeconds = (int)run.Duration.TotalSeconds,
            next = page.Next,
            skipped = page.Skipped,
            resync = page.Resync,
            lines = page.Lines.Select(l => new { t = l.Timestamp, e = l.IsError, x = l.Text })
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
    public async Task<IActionResult> Browse([FromQuery] string? path, [FromQuery] string mode = "file", [FromQuery] string? extension = null)
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
            // Off the request thread and abortable: a share with 100 000s of files must not block a worker thread.
            var listing = await Task.Run(() => ListDirectory(full, !mode.Equals("folder", StringComparison.OrdinalIgnoreCase), extension, HttpContext.RequestAborted),
                                         HttpContext.RequestAborted);
            var parent = Directory.GetParent(full)?.FullName;
            if (parent != null && !_pathPolicy.CanBrowse(parent)) parent = null;
            return Ok(new { path = full, parent, roots, entries = listing.Entries, truncated = listing.Truncated });
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

    /// <summary>
    /// Directory listing for the chooser: filters while enumerating and stops at
    /// <see cref="MaxBrowseEntries"/> — only that subset is sorted, so huge folders stay cheap.
    /// </summary>
    internal static (List<object> Entries, bool Truncated) ListDirectory(string directory, bool includeFiles, string? extension, CancellationToken ct)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        var dir = new DirectoryInfo(directory);

        var dirs = dir.EnumerateDirectories("*", options).Take(MaxBrowseEntries + 1).ToList();
        ct.ThrowIfCancellationRequested();
        var truncated = dirs.Count > MaxBrowseEntries;
        if (truncated) dirs.RemoveAt(dirs.Count - 1);

        var files = new List<FileInfo>();
        if (includeFiles && !truncated)
        {
            var pattern = string.IsNullOrEmpty(extension) ? "*" : "*" + extension;
            files = dir.EnumerateFiles(pattern, options)
                // "*.ps1" also matches e.g. ".ps1xml" on Windows (8.3 legacy) — check exactly.
                .Where(f => string.IsNullOrEmpty(extension) || f.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Take(MaxBrowseEntries - dirs.Count + 1)
                .ToList();
            ct.ThrowIfCancellationRequested();
            if (files.Count > MaxBrowseEntries - dirs.Count)
            {
                truncated = true;
                files.RemoveAt(files.Count - 1);
            }
        }

        var entries = new List<object>(dirs.Count + files.Count);
        entries.AddRange(dirs.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => (object)new { name = d.Name, fullPath = d.FullName, isDirectory = true }));
        entries.AddRange(files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => (object)new { name = f.Name, fullPath = f.FullName, isDirectory = false, size = f.Length, modified = f.LastWriteTime }));
        return (entries, truncated);
    }
}
