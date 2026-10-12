using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;

namespace Minicon.SimpleAdmin.WebUI.Services.Scripts;

/// <summary>
/// Thrown when a run cannot be started (validation, concurrency, path policy).
/// </summary>
public sealed class ScriptRunException : Exception
{
    public ScriptRunException(IReadOnlyList<string> errors) : base(string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Starts PowerShell scripts as child processes of the WebUI, captures their output line by
/// line (memory + log file), enforces timeouts and keeps an audit history
/// (<c>history.json</c>: who started what with which parameters, exit code, duration).
/// </summary>
public sealed class ScriptRunService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ScriptRunnerOptions _options;
    private readonly ScriptPathPolicy _pathPolicy;
    private readonly ILogger<ScriptRunService> _logger;
    private readonly string _logDirectory;
    private readonly object _lock = new();
    private readonly List<ScriptRun> _history;
    private readonly ConcurrentDictionary<string, RunState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: builds the process start info (default: PowerShell + bootstrap).</summary>
    internal Func<ScriptDefinition, string, ProcessStartInfo>? StartInfoFactory { get; set; }

    public ScriptRunService(ScriptRunnerOptions options, ScriptPathPolicy pathPolicy, string logDirectory, ILogger<ScriptRunService> logger)
    {
        _options = options;
        _pathPolicy = pathPolicy;
        _logger = logger;
        _logDirectory = logDirectory;
        _history = LoadHistory();
    }

    /// <summary>All runs, newest first.</summary>
    public IReadOnlyList<ScriptRun> GetHistory(string? scriptId = null, int max = 50)
    {
        lock (_lock)
        {
            return _history
                .Where(r => scriptId == null || r.ScriptId.Equals(scriptId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.StartedAt)
                .Take(max)
                .Select(Copy)
                .ToList();
        }
    }

    public ScriptRun? GetRun(string runId)
    {
        lock (_lock)
        {
            var r = _history.FirstOrDefault(x => x.Id.Equals(runId, StringComparison.OrdinalIgnoreCase));
            return r == null ? null : Copy(r);
        }
    }

    /// <summary>
    /// Validates the input and starts the script. Throws <see cref="ScriptRunException"/> with
    /// German messages when the run is not possible.
    /// </summary>
    public ScriptRun Start(ScriptDefinition definition, IReadOnlyDictionary<string, List<string>> input, string? user)
    {
        var pathError = _pathPolicy.ValidateScriptPath(definition.Path);
        if (pathError != null) throw new ScriptRunException(new[] { pathError });
        if (!File.Exists(definition.Path)) throw new ScriptRunException(new[] { $"Skript nicht gefunden: {definition.Path}" });

        var (arguments, errors) = ScriptArgumentBuilder.Build(definition, input);
        if (errors.Count > 0) throw new ScriptRunException(errors);

        ScriptRun run;
        RunState state;
        lock (_lock)
        {
            var running = _history.Where(r => r.IsRunning).ToList();
            if (!definition.AllowParallelRuns && running.Any(r => r.ScriptId == definition.Id))
                throw new ScriptRunException(new[] { $"'{definition.Name}' läuft bereits. Bitte warten oder den laufenden Lauf abbrechen." });
            if (running.Count >= Math.Max(1, _options.MaxConcurrentRuns))
                throw new ScriptRunException(new[] { $"Es laufen bereits {running.Count} Skripte (Maximum {_options.MaxConcurrentRuns})." });

            run = new ScriptRun
            {
                Id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                ScriptId = definition.Id,
                ScriptName = definition.Name,
                ScriptPath = definition.Path,
                Arguments = arguments.ToDictionary(a => a.Name, a => a.Display),
                InputValues = input.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                StartedBy = user,
                StartedAt = DateTime.UtcNow,
                Status = ScriptRunStatus.Running
            };
            state = new RunState(run, _options.MaxOutputLines);
            _history.Add(run);
            _states[run.Id] = state;
            TrimHistory();
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            state.Log = new StreamWriter(LogPath(run.Id), append: false, new UTF8Encoding(false)) { AutoFlush = true };
            WriteHeader(state, definition);

            var psi = (StartInfoFactory ?? BuildStartInfo)(definition, ScriptArgumentBuilder.Encode(arguments));
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) state.Append(false, e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) state.Append(true, e.Data); };

            if (!process.Start()) throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
            state.Process = process;
            lock (_lock) run.ProcessId = process.Id;
            if (psi.RedirectStandardInput) process.StandardInput.Close(); // Read-Host must fail, not hang
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger.LogInformation("Script run {RunId} started: {Script} ({Path}) by {User}, PID {Pid}",
                run.Id, definition.Name, definition.Path, user ?? "(anonymous)", process.Id);
            PersistHistory();

            _ = MonitorAsync(state, definition.TimeoutMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script run {RunId} could not be started", run.Id);
            state.Append(true, $"Start fehlgeschlagen: {ex.Message}");
            Finish(state, ScriptRunStatus.Failed, null, $"Start fehlgeschlagen: {ex.Message}");
        }

        return Copy(run);
    }

    /// <summary>Kills a running script (incl. child processes).</summary>
    public bool Cancel(string runId, string? user)
    {
        if (!_states.TryGetValue(runId, out var state) || !state.Run.IsRunning) return false;
        lock (_lock) state.Run.CancelledBy = user;
        state.CancelRequested = true;
        state.Append(true, $"Abbruch angefordert von {user ?? "WebUI"}.");
        try
        {
            state.Process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill script run {RunId}", runId);
        }
        return true;
    }

    /// <summary>
    /// Output lines from <paramref name="from"/> on (absolute line index).
    /// </summary>
    public (IReadOnlyList<ScriptOutputLine> Lines, int Next, int Skipped) GetOutput(string runId, int from)
    {
        if (_states.TryGetValue(runId, out var state)) return state.Read(from);

        // Older run: read the log file.
        var path = LogPath(runId);
        if (!File.Exists(path)) return (Array.Empty<ScriptOutputLine>(), from, 0);
        var lines = new List<ScriptOutputLine>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = ParseLogLine(raw);
            if (line != null) lines.Add(line);
        }
        var start = Math.Clamp(from, 0, lines.Count);
        return (lines.Skip(start).Take(5000).ToList(), Math.Min(lines.Count, start + 5000), 0);
    }

    public string LogPath(string runId)
    {
        var safe = string.Concat(runId.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        return Path.Combine(_logDirectory, safe + ".log");
    }

    // ── internals ──────────────────────────────────────────────────────────

    private ProcessStartInfo BuildStartInfo(ScriptDefinition definition, string encodedArguments)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(definition.WorkingDirectory)
            ? definition.WorkingDirectory!
            : Path.GetDirectoryName(definition.Path) ?? Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = _options.EffectivePowerShellPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ArgumentList: every entry is quoted by .NET — no user input is part of it anyway.
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                  "-OutputFormat", "Text", "-EncodedCommand", ScriptArgumentBuilder.EncodedBootstrap })
            psi.ArgumentList.Add(a);
        psi.Environment[ScriptArgumentBuilder.PathVariable] = Path.GetFullPath(definition.Path);
        psi.Environment[ScriptArgumentBuilder.ArgsVariable] = encodedArguments;
        return psi;
    }

    private async Task MonitorAsync(RunState state, int timeoutMinutes)
    {
        var process = state.Process!;
        var timedOut = false;
        try
        {
            if (timeoutMinutes > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    state.Append(true, $"Zeitlimit von {timeoutMinutes} Minuten überschritten — Skript wird beendet.");
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    await process.WaitForExitAsync();
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }

            var exitCode = process.ExitCode;
            var status = timedOut ? ScriptRunStatus.TimedOut
                : state.CancelRequested ? ScriptRunStatus.Cancelled
                : exitCode == 0 ? ScriptRunStatus.Succeeded
                : ScriptRunStatus.Failed;
            Finish(state, status, exitCode, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring of script run {RunId} failed", state.Run.Id);
            Finish(state, ScriptRunStatus.Failed, null, ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Finish(RunState state, ScriptRunStatus status, int? exitCode, string? error)
    {
        lock (_lock)
        {
            state.Run.Status = status;
            state.Run.ExitCode = exitCode;
            state.Run.Error = error;
            state.Run.FinishedAt = DateTime.UtcNow;
        }
        try
        {
            state.Log?.WriteLine(FormatLogLine(new ScriptOutputLine(DateTime.UtcNow, status != ScriptRunStatus.Succeeded,
                $"=== Beendet: {StatusText(status)}{(exitCode.HasValue ? $", Exit-Code {exitCode}" : "")} ===")));
            state.Log?.Dispose();
            state.Log = null;
        }
        catch { /* best effort */ }

        _logger.LogInformation("Script run {RunId} finished: {Status}, exit code {ExitCode}", state.Run.Id, status, exitCode);
        PersistHistory();

        // Keep the last finished runs in memory for fast output polling; older ones are read from the log file.
        var finished = _states.Values.Where(s => !s.Run.IsRunning).OrderByDescending(s => s.Run.FinishedAt).Skip(20).ToList();
        foreach (var old in finished) _states.TryRemove(old.Run.Id, out _);
    }

    internal static string StatusText(ScriptRunStatus status) => status switch
    {
        ScriptRunStatus.Running => "Läuft",
        ScriptRunStatus.Succeeded => "Erfolgreich",
        ScriptRunStatus.Failed => "Fehlgeschlagen",
        ScriptRunStatus.Cancelled => "Abgebrochen",
        ScriptRunStatus.TimedOut => "Zeitüberschreitung",
        ScriptRunStatus.Interrupted => "Unterbrochen",
        _ => status.ToString()
    };

    private void WriteHeader(RunState state, ScriptDefinition definition)
    {
        var run = state.Run;
        state.Append(false, $"=== {definition.Name} — gestartet von {run.StartedBy ?? "WebUI"} am {run.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss} ===");
        state.Append(false, $"Skript: {definition.Path}");
        foreach (var kv in run.Arguments)
            state.Append(false, $"  -{kv.Key} {kv.Value}");
        state.Append(false, string.Empty);
    }

    private void TrimHistory()
    {
        var max = Math.Max(10, _options.HistorySize);
        var excess = _history.Where(r => !r.IsRunning).OrderBy(r => r.StartedAt).Take(Math.Max(0, _history.Count - max)).ToList();
        foreach (var r in excess)
        {
            _history.Remove(r);
            try { File.Delete(LogPath(r.Id)); } catch { /* best effort */ }
        }
    }

    private string HistoryPath => Path.Combine(_logDirectory, "history.json");

    private List<ScriptRun> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<ScriptRun>();
            var list = JsonSerializer.Deserialize<List<ScriptRun>>(File.ReadAllText(HistoryPath), JsonOptions) ?? new List<ScriptRun>();
            // Runs still marked "running" belong to a previous WebUI process (recycle/restart).
            foreach (var r in list.Where(r => r.IsRunning))
            {
                r.Status = ScriptRunStatus.Interrupted;
                r.FinishedAt ??= DateTime.UtcNow;
                r.Error = "WebUI wurde während des Laufs neu gestartet (z. B. IIS-Recycle).";
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read script run history {Path}", HistoryPath);
            return new List<ScriptRun>();
        }
    }

    private void PersistHistory()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_history, JsonOptions);
            Directory.CreateDirectory(_logDirectory);
            lock (_historyFileLock)
            {
                var tmp = HistoryPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, HistoryPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write script run history {Path}", HistoryPath);
        }
    }

    private readonly object _historyFileLock = new();

    private static ScriptRun Copy(ScriptRun r) =>
        JsonSerializer.Deserialize<ScriptRun>(JsonSerializer.Serialize(r, JsonOptions), JsonOptions)!;

    internal static string FormatLogLine(ScriptOutputLine line) =>
        $"{line.Timestamp.ToString("O", CultureInfo.InvariantCulture)}|{(line.IsError ? 'E' : 'O')}|{line.Text}";

    internal static ScriptOutputLine? ParseLogLine(string raw)
    {
        var first = raw.IndexOf('|');
        if (first < 0 || first + 2 >= raw.Length || raw[first + 2] != '|') return null;
        if (!DateTime.TryParse(raw[..first], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) return null;
        return new ScriptOutputLine(ts, raw[first + 1] == 'E', raw[(first + 3)..]);
    }

    public void Dispose()
    {
        foreach (var s in _states.Values.Where(s => s.Run.IsRunning))
        {
            try { s.Process?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex AnsiEscape =
        new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed class RunState
    {
        private readonly int _maxLines;
        private readonly List<ScriptOutputLine> _lines = new();
        private int _offset; // absolute index of _lines[0]

        public RunState(ScriptRun run, int maxLines)
        {
            Run = run;
            _maxLines = Math.Max(100, maxLines);
        }

        public ScriptRun Run { get; }
        public Process? Process { get; set; }
        public StreamWriter? Log { get; set; }
        public volatile bool CancelRequested;

        public void Append(bool isError, string text)
        {
            var line = new ScriptOutputLine(DateTime.UtcNow, isError, AnsiEscape.Replace(text, string.Empty));
            lock (_lines)
            {
                _lines.Add(line);
                if (_lines.Count > _maxLines)
                {
                    var drop = _lines.Count - _maxLines;
                    _lines.RemoveRange(0, drop);
                    _offset += drop;
                }
                try { Log?.WriteLine(FormatLogLine(line)); } catch { /* disk full etc. — keep running */ }
            }
        }

        public (IReadOnlyList<ScriptOutputLine> Lines, int Next, int Skipped) Read(int from)
        {
            lock (_lines)
            {
                var skipped = Math.Max(0, _offset - from);
                var start = Math.Max(0, from - _offset);
                if (start > _lines.Count) start = _lines.Count;
                var take = Math.Min(5000, _lines.Count - start);
                return (_lines.GetRange(start, take), _offset + start + take, skipped);
            }
        }
    }
}
