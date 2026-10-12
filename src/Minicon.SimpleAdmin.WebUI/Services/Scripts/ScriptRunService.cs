using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// A slice of a run's output for the Output page.
/// </summary>
/// <param name="FirstIndex">Absolute index of <c>Lines[0]</c>; -1 when read from the log file (unknown).</param>
/// <param name="Next">Index to pass as <c>from</c> in the next request.</param>
/// <param name="Skipped">Lines before <c>Lines[0]</c> not delivered (only in the log file); -1 = some, count unknown.</param>
/// <param name="Resync">The requested position is no longer in memory — reload the tail.</param>
public sealed record ScriptOutputPage(IReadOnlyList<ScriptOutputLine> Lines, int FirstIndex, int Next, int Skipped, bool Resync);

/// <summary>
/// Starts PowerShell scripts as child processes of the WebUI and keeps an audit history
/// (<c>history.json</c>: who started what with which parameters, exit code, duration).
/// <list type="bullet">
/// <item>Output is captured line by line into a fixed-size ring buffer (live view) and a buffered
/// log file (complete record, flushed every second). Finished runs stay in memory for
/// <see cref="ScriptRunnerOptions.FinishedRunMemoryRetention"/>; after that only the file is used.</item>
/// <item>A run ends when the process exits — open output pipes held by background processes of the
/// script delay this by at most <see cref="StreamDrainGrace"/>.</item>
/// <item>history.json is merged on every write (newest <see cref="ScriptRun.UpdatedAt"/> per run wins) under a
/// file lock, so two WebUI processes during an IIS overlapped recycle do not overwrite each other.</item>
/// </list>
/// </summary>
public sealed class ScriptRunService : IDisposable
{
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly ScriptRunnerOptions _options;
    private readonly ScriptPathPolicy _pathPolicy;
    private readonly ILogger<ScriptRunService> _logger;
    private readonly string _logDirectory;
    private readonly object _lock = new();
    private readonly object _historyFileLock = new();
    private List<ScriptRun> _history;
    private readonly ConcurrentDictionary<string, RunState> _states = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    /// <summary>Test seam: builds the process start info (default: PowerShell + bootstrap).</summary>
    internal Func<ScriptDefinition, string, ProcessStartInfo>? StartInfoFactory { get; set; }

    /// <summary>Test seam: length of one "minute" of <see cref="ScriptDefinition.TimeoutMinutes"/>.</summary>
    internal TimeSpan TimeoutUnit { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait for stdout/stderr EOF after the process exited.</summary>
    internal TimeSpan StreamDrainGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the process to die after a kill.</summary>
    internal TimeSpan KillWait { get; set; } = TimeSpan.FromSeconds(30);

    public ScriptRunService(ScriptRunnerOptions options, ScriptPathPolicy pathPolicy, string logDirectory, ILogger<ScriptRunService> logger)
    {
        _options = options;
        _pathPolicy = pathPolicy;
        _logger = logger;
        _logDirectory = logDirectory;
        _history = ReadHistoryFile();

        // Runs still marked "running" belong to a previous WebUI process (recycle/restart). If that
        // process is in fact still alive (overlapped recycle) its later, newer update wins the merge.
        var interrupted = false;
        foreach (var r in _history.Where(r => r.IsRunning))
        {
            r.Status = ScriptRunStatus.Interrupted;
            r.FinishedAt ??= DateTime.UtcNow;
            r.UpdatedAt = DateTime.UtcNow;
            r.Error = "WebUI wurde während des Laufs neu gestartet (z. B. IIS-Recycle).";
            interrupted = true;
        }
        if (interrupted) PersistHistory();
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
                .Select(r => r.Clone())
                .ToList();
        }
    }

    public ScriptRun? GetRun(string runId)
    {
        lock (_lock)
        {
            return _history.FirstOrDefault(x => x.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>
    /// Validates the input and starts the script. Throws <see cref="ScriptRunException"/> with
    /// German messages when the run is not possible.
    /// </summary>
    public ScriptRun Start(ScriptDefinition definition, IReadOnlyDictionary<string, List<string>> input, string? user)
    {
        if (_disposed) throw new ScriptRunException(new[] { "Die WebUI wird gerade beendet." });
        var pathError = _pathPolicy.ValidateScriptPath(definition.Path);
        if (pathError != null) throw new ScriptRunException(new[] { pathError });
        if (!File.Exists(definition.Path)) throw new ScriptRunException(new[] { $"Skript nicht gefunden: {definition.Path}" });

        var (arguments, errors) = ScriptArgumentBuilder.Build(definition, input);
        if (errors.Count > 0) throw new ScriptRunException(errors);

        EvictFinishedStates();

        ScriptRun run;
        RunState state;
        lock (_lock)
        {
            // Only runs of this process count — a "running" entry written by another process cannot block us.
            var running = _states.Values.Where(s => s.Run.IsRunning).Select(s => s.Run).ToList();
            if (!definition.AllowParallelRuns && running.Any(r => r.ScriptId == definition.Id))
                throw new ScriptRunException(new[] { $"'{definition.Name}' läuft bereits. Bitte warten oder den laufenden Lauf abbrechen." });
            if (running.Count >= Math.Max(1, _options.MaxConcurrentRuns))
                throw new ScriptRunException(new[] { $"Es laufen bereits {running.Count} Skripte (Maximum {_options.MaxConcurrentRuns})." });

            var now = DateTime.UtcNow;
            run = new ScriptRun
            {
                Id = $"{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                ScriptId = definition.Id,
                ScriptName = definition.Name,
                ScriptPath = definition.Path,
                Arguments = arguments.ToDictionary(a => a.Name, a => a.Display),
                CommandLine = ScriptArgumentBuilder.FormatCommandLine(definition.Path, arguments),
                InputValues = input.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                StartedBy = user,
                StartedAt = now,
                UpdatedAt = now,
                Status = ScriptRunStatus.Running
            };
            state = new RunState(run, _options.MaxOutputLines);
            _history.Add(run);
            _states[run.Id] = state;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            state.OpenLog(LogPath(run.Id));
            WriteHeader(state, definition);

            var psi = (StartInfoFactory ?? BuildStartInfo)(definition, ScriptArgumentBuilder.Encode(arguments));
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) state.Append(false, e.Data); else state.StdoutDone.TrySetResult(); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) state.Append(true, e.Data); else state.StderrDone.TrySetResult(); };
            process.Exited += (_, _) => state.Exited.TrySetResult();

            if (!process.Start()) throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
            state.Process = process;
            lock (_lock) run.ProcessId = process.Id;
            if (psi.RedirectStandardInput) process.StandardInput.Close(); // Read-Host must fail, not hang
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) state.Exited.TrySetResult();

            // A cancel that arrived before the process existed (Cancel sets the flag first, then reads Process).
            if (state.CancelRequested) TryKill(process, run.Id);

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

        return run.Clone();
    }

    /// <summary>Kills a running script (incl. child processes).</summary>
    public bool Cancel(string runId, string? user)
    {
        if (!_states.TryGetValue(runId, out var state) || !state.Run.IsRunning) return false;
        lock (_lock) state.Run.CancelledBy = user;
        state.CancelRequested = true;
        state.Append(true, $"Abbruch angefordert von {user ?? "WebUI"}.");
        var process = state.Process;
        if (process != null) TryKill(process, runId);
        return true;
    }

    /// <summary>
    /// Output of a run. <paramref name="from"/> = null: the last <see cref="ScriptRunnerOptions.TailLines"/>
    /// lines (initial load); otherwise the lines from that absolute index on (live polling).
    /// </summary>
    public ScriptOutputPage GetOutput(string runId, int? from)
    {
        if (_states.TryGetValue(runId, out var state))
            return from.HasValue ? state.Read(from.Value) : state.Tail(ScriptRunnerOptions.TailLines);

        // Not in memory (finished a while ago or before a restart): the run is final → serve the tail of the log file.
        if (from.HasValue) return new ScriptOutputPage(Array.Empty<ScriptOutputLine>(), from.Value, from.Value, 0, Resync: true);
        var (lines, complete) = ReadLogTail(LogPath(runId), ScriptRunnerOptions.TailLines);
        return new ScriptOutputPage(lines, -1, -1, complete ? 0 : -1, Resync: false);
    }

    public string LogPath(string runId)
    {
        var safe = string.Concat(runId.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        return Path.Combine(_logDirectory, safe + ".log");
    }

    // ── process handling ───────────────────────────────────────────────────

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
            // Wait for the process itself — NOT WaitForExitAsync, which also waits for EOF on the
            // redirected pipes and therefore hangs as long as a background child keeps them open.
            if (timeoutMinutes > 0)
            {
                var limit = TimeoutUnit * timeoutMinutes;
                if (await Task.WhenAny(state.Exited.Task, Task.Delay(limit)) != state.Exited.Task)
                {
                    timedOut = true;
                    state.Append(true, $"Zeitlimit von {timeoutMinutes} Minuten überschritten — Skript wird beendet.");
                    TryKill(process, state.Run.Id);
                    await Task.WhenAny(state.Exited.Task, Task.Delay(KillWait));
                }
            }
            else
            {
                await state.Exited.Task;
            }

            var drained = Task.WhenAll(state.StdoutDone.Task, state.StderrDone.Task);
            if (await Task.WhenAny(drained, Task.Delay(StreamDrainGrace)) != drained)
                state.Append(true, "Hinweis: Die Ausgabe ist nach Prozessende noch geöffnet (Hintergrundprozess des Skripts?) — der Lauf wird trotzdem abgeschlossen.");

            int? exitCode = process.HasExited ? process.ExitCode : null;
            var status = timedOut ? ScriptRunStatus.TimedOut
                : state.CancelRequested ? ScriptRunStatus.Cancelled
                : exitCode == 0 ? ScriptRunStatus.Succeeded
                : ScriptRunStatus.Failed;
            Finish(state, status, exitCode, exitCode == null ? "Prozess ließ sich nicht beenden." : null);
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

    private void TryKill(Process process, string runId)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill script run {RunId}", runId);
        }
    }

    private void Finish(RunState state, ScriptRunStatus status, int? exitCode, string? error)
    {
        if (!state.TryMarkFinished()) return;

        state.Append(status != ScriptRunStatus.Succeeded,
            $"=== Beendet: {StatusText(status)}{(exitCode.HasValue ? $", Exit-Code {exitCode}" : "")} ===");
        state.CloseLog();

        lock (_lock)
        {
            state.Run.Status = status;
            state.Run.ExitCode = exitCode;
            state.Run.Error = error;
            state.Run.FinishedAt = DateTime.UtcNow;
            state.Run.UpdatedAt = DateTime.UtcNow;
        }

        _logger.LogInformation("Script run {RunId} finished: {Status}, exit code {ExitCode}", state.Run.Id, status, exitCode);
        PersistHistory();
        EvictFinishedStates();
    }

    /// <summary>Drops in-memory output of runs that finished longer than the retention ago (the log file has it all).</summary>
    private void EvictFinishedStates()
    {
        var cutoff = DateTime.UtcNow - ScriptRunnerOptions.FinishedRunMemoryRetention;
        foreach (var s in _states.Values)
            if (s.FinishedAtUtc is { } f && f < cutoff)
                _states.TryRemove(s.Run.Id, out _);
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

    private static void WriteHeader(RunState state, ScriptDefinition definition)
    {
        var run = state.Run;
        state.Append(false, $"=== {definition.Name} — gestartet von {run.StartedBy ?? "WebUI"} am {run.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss} ===");
        state.Append(false, $"Skript: {definition.Path}");
        state.Append(false, $"Aufruf: {run.CommandLine}");
        state.Append(false, string.Empty);
    }

    // ── history.json ───────────────────────────────────────────────────────

    private string HistoryPath => Path.Combine(_logDirectory, "history.json");

    private List<ScriptRun> ReadHistoryFile()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<ScriptRun>();
            return JsonSerializer.Deserialize<List<ScriptRun>>(File.ReadAllText(HistoryPath), ScriptJson.Options) ?? new List<ScriptRun>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read script run history {Path}", HistoryPath);
            return new List<ScriptRun>();
        }
    }

    /// <summary>
    /// Merges the in-memory history with history.json (newest <see cref="ScriptRun.UpdatedAt"/> per run
    /// wins; live runs of this process always keep their object), trims it and writes it back.
    /// </summary>
    private void PersistHistory()
    {
        var deleteLogs = new List<string>();
        try
        {
            Directory.CreateDirectory(_logDirectory);
            lock (_historyFileLock)
            {
                using var fileLock = AcquireFileLock();
                var disk = ReadHistoryFile();
                string json;
                lock (_lock)
                {
                    var merged = new Dictionary<string, ScriptRun>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in disk) merged[r.Id] = r;
                    foreach (var r in _history)
                    {
                        var live = _states.ContainsKey(r.Id);
                        if (live || !merged.TryGetValue(r.Id, out var other) || Stamp(r) >= Stamp(other))
                            merged[r.Id] = r;
                    }

                    var ordered = merged.Values.OrderByDescending(r => r.StartedAt).ToList();
                    var max = Math.Max(10, _options.HistorySize);
                    var keep = ordered.Where((r, i) => i < max || r.IsRunning).ToList();
                    deleteLogs.AddRange(ordered.Except(keep).Select(r => r.Id));
                    _history = keep;
                    json = JsonSerializer.Serialize(keep, ScriptJson.Options);
                }

                var tmp = HistoryPath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, HistoryPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write script run history {Path}", HistoryPath);
        }

        foreach (var id in deleteLogs)
        {
            try { File.Delete(LogPath(id)); } catch { /* best effort */ }
        }

        static DateTime Stamp(ScriptRun r) => r.UpdatedAt ?? r.FinishedAt ?? r.StartedAt;
    }

    /// <summary>Cross-process lock for history.json (best effort: after ~3 s it proceeds without).</summary>
    private IDisposable? AcquireFileLock()
    {
        var path = HistoryPath + ".lock";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        _logger.LogWarning("Could not lock {Path}; writing history without lock", path);
        return null;
    }

    // ── log file ───────────────────────────────────────────────────────────

    /// <summary>"2026-10-13 14:05:01.123 | text" (stdout) / "… ! text" (stderr), local time — readable as downloaded.</summary>
    internal static string FormatLogLine(ScriptOutputLine line) =>
        $"{line.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {(line.IsError ? '!' : '|')} {line.Text}";

    internal static ScriptOutputLine? ParseLogLine(string raw)
    {
        // Current format: fixed 23-char local timestamp, marker, text.
        if (raw.Length >= 25 && raw[23] == ' ' && raw[24] is '|' or '!'
            && DateTime.TryParseExact(raw[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return new ScriptOutputLine(local.ToUniversalTime(), raw[24] == '!', raw.Length > 26 ? raw[26..] : string.Empty);
        }

        // v2.31.0 format: "<ISO-8601 UTC>|O|text" / "|E|".
        var first = raw.IndexOf('|');
        if (first < 0 || first + 2 >= raw.Length || raw[first + 2] != '|') return null;
        if (!DateTime.TryParse(raw[..first], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) return null;
        return new ScriptOutputLine(ts, raw[first + 1] == 'E', raw[(first + 3)..]);
    }

    /// <summary>
    /// Last <paramref name="count"/> lines of a log file, read backwards from the end — the cost does not
    /// depend on the file size. <c>Complete</c> = the whole file was read (no older lines exist).
    /// </summary>
    internal static (List<ScriptOutputLine> Lines, bool Complete) ReadLogTail(string path, int count)
    {
        if (!File.Exists(path)) return (new List<ScriptOutputLine>(), true);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int block = 64 * 1024;
        var maxBytes = (long)count * (ScriptRunnerOptions.MaxLineLength + 64);
        var pos = fs.Length;
        var newlines = 0;
        var buffer = new byte[block];
        while (pos > 0 && newlines <= count && fs.Length - pos < maxBytes)
        {
            var size = (int)Math.Min(block, pos);
            pos -= size;
            fs.Position = pos;
            fs.ReadExactly(buffer, 0, size);
            for (var i = 0; i < size; i++)
                if (buffer[i] == (byte)'\n') newlines++;
        }

        fs.Position = pos;
        var bytes = new byte[fs.Length - pos];
        fs.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        var rawLines = text.Split('\n');
        var startIndex = pos > 0 ? 1 : 0; // first piece may be a partial line
        var lines = new List<ScriptOutputLine>();
        for (var i = startIndex; i < rawLines.Length; i++)
        {
            var parsed = ParseLogLine(rawLines[i].TrimEnd('\r'));
            if (parsed != null) lines.Add(parsed);
        }

        var complete = pos == 0 && lines.Count <= count;
        return (lines.Count > count ? lines.GetRange(lines.Count - count, count) : lines, complete);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // App shutdown / recycle: our child processes die with us — record them as interrupted.
        foreach (var s in _states.Values.Where(s => s.Run.IsRunning))
        {
            Finish(s, ScriptRunStatus.Interrupted, null, "WebUI wurde während des Laufs beendet (z. B. IIS-Recycle).");
            var process = s.Process;
            if (process != null) TryKill(process, s.Run.Id);
        }
    }

    // ── per-run state ──────────────────────────────────────────────────────

    private sealed class RunState
    {
        private readonly object _sync = new();
        private readonly ScriptOutputLine[] _ring;
        private int _start;   // ring index of the oldest line
        private int _count;   // lines currently in the ring
        private int _total;   // lines ever appended (absolute index of the next line)
        private StreamWriter? _log;
        private Timer? _flushTimer;
        private int _finished;
        private volatile Process? _process;

        public RunState(ScriptRun run, int maxLines)
        {
            Run = run;
            _ring = new ScriptOutputLine[Math.Max(100, maxLines)];
        }

        public ScriptRun Run { get; }
        public volatile bool CancelRequested;
        public Process? Process { get => _process; set => _process = value; }
        public DateTime? FinishedAtUtc { get; private set; }
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StdoutDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StderrDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OpenLog(string path)
        {
            lock (_sync)
            {
                // Buffered (no AutoFlush): one syscall per line would throttle chatty scripts; flushed every second.
                _log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete),
                                        new UTF8Encoding(false), 64 * 1024) { NewLine = "\n" };
                _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        public bool TryMarkFinished()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1) return false;
            FinishedAtUtc = DateTime.UtcNow;
            return true;
        }

        public void Append(bool isError, string text)
        {
            text = AnsiEscape.Replace(text, string.Empty);
            if (text.Length > ScriptRunnerOptions.MaxLineLength)
                text = text[..ScriptRunnerOptions.MaxLineLength] + $" … [gekürzt, {text.Length:N0} Zeichen]";
            var line = new ScriptOutputLine(DateTime.UtcNow, isError, text);

            lock (_sync)
            {
                if (_count < _ring.Length)
                {
                    _ring[(_start + _count) % _ring.Length] = line;
                    _count++;
                }
                else
                {
                    _ring[_start] = line; // overwrite the oldest — O(1), no shifting
                    _start = (_start + 1) % _ring.Length;
                }
                _total++;
                try { _log?.WriteLine(FormatLogLine(line)); } catch { /* disk full etc. — keep running */ }
            }
        }

        public ScriptOutputPage Read(int from)
        {
            lock (_sync)
            {
                var oldest = _total - _count;
                if (from > _total) from = _total;
                var skipped = Math.Max(0, oldest - from);
                var start = Math.Max(from, oldest);
                var take = Math.Min(5000, _total - start);
                return new ScriptOutputPage(Copy(start, take), start, start + take, skipped, Resync: false);
            }
        }

        public ScriptOutputPage Tail(int n)
        {
            lock (_sync)
            {
                var take = Math.Min(n, _count);
                var start = _total - take;
                return new ScriptOutputPage(Copy(start, take), start, _total, start, Resync: false);
            }
        }

        private List<ScriptOutputLine> Copy(int absoluteStart, int take)
        {
            var oldest = _total - _count;
            var list = new List<ScriptOutputLine>(take);
            for (var i = 0; i < take; i++)
                list.Add(_ring[(_start + (absoluteStart - oldest) + i) % _ring.Length]);
            return list;
        }

        private void Flush()
        {
            lock (_sync)
            {
                try { _log?.Flush(); } catch { /* best effort */ }
            }
        }

        public void CloseLog()
        {
            lock (_sync)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                try { _log?.Dispose(); } catch { /* best effort */ }
                _log = null; // late output (background child) still reaches the ring buffer, not the closed file
            }
        }
    }
}
