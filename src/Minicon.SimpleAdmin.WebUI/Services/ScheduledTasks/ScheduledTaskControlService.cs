using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;

namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// Enables/disables/starts/stops Windows Scheduled Tasks directly on the target server from the WebUI process.
/// <list type="bullet">
/// <item>Mechanism: PowerShell ScheduledTasks cmdlets over a CIM session with <c>-Protocol Dcom</c> (only RPC/DCOM
/// like taskschd.msc, no WinRM); the local machine is addressed without a session. Not <c>schtasks.exe</c>: its
/// CSV/LIST output is localized (German Windows: column names, "Bereit"/"Deaktiviert", date formats) and cannot be
/// parsed reliably. The script emits Base64(UTF-8 JSON) with culture-invariant UTC dates instead.</item>
/// <item>Injection-safe: server and task path are validated (no process start otherwise) and only enter the
/// script as <see cref="PowerShellLiteral.Quote"/> literals; the script runs via <c>-EncodedCommand</c>.</item>
/// <item>Serialization: Enable/Disable and "activate exclusively" on one task path (any server) run one after
/// another, so a concurrent single Enable cannot slip between the disable and enable steps. Run/End are serialized
/// per (server, task path) only. Every call takes exactly one lock (no ordering, no deadlock). This
/// is guaranteed only within this WebUI process; across several WebUI processes (e.g. two hosts) it is not, but a
/// task enabled twice is then detected by the central single-active check of the group.</item>
/// <item>Cancellation: <c>ct</c> is honoured while waiting for the lock and for queries. Once a change has started
/// it runs to completion with its own timeout, so an aborted HTTP request cannot leave a half-done state (e.g. the
/// task disabled on every server).</item>
/// <item>Every mutating call is audited (<see cref="ScheduledTaskAuditLog"/>); an audit failure is noted in the
/// result text but does not fail the action.</item>
/// </list>
/// </summary>
public sealed class ScheduledTaskControlService : IScheduledTaskControl
{
    private const string ResultMarker = "SA-RESULT:";
    private const string QueryAction = "Query";
    private const string ExclusiveAction = "ActivateExclusively";

    /// <summary>Maximum wait for the per-task lock before answering "another action is running".</summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-step timeout of "activate exclusively" (capped by <see cref="ScheduledTaskControlOptions.ActionTimeoutSeconds"/>).
    /// Worst case per step: 20 s timeout + <see cref="ScheduledTaskProcessRunner.KillWait"/> 2 s +
    /// <see cref="ScheduledTaskProcessRunner.StreamDrainGrace"/> 3 s = 25 s. Whole operation: lock wait 10 s
    /// + target check 25 s + parallel disables 25 s + audit ≤ 3 s + enable 25 s + audit ≤ 3 s + summary audit ≤ 3 s
    /// = 94 s (the audit waits only occur when another process holds the audit file lock), below the default
    /// 2 min request timeout of IIS / the ASP.NET Core module. The disables are therefore audited as one batch.
    /// </summary>
    internal static readonly TimeSpan ExclusiveStepTimeoutCap = TimeSpan.FromSeconds(20);

    private static readonly Regex ServerPattern = new("^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ScheduledTaskControlOptions _options;
    private readonly ScheduledTaskAuditLog _audit;
    private readonly ILogger<ScheduledTaskControlService> _logger;
    private readonly IScheduledTaskProcessRunner _runner;
    private readonly KeyedAsyncLock _taskLocks = new();

    /// <summary>Test seam: whether the platform can run the ScheduledTasks cmdlets (default: Windows).</summary>
    internal Func<bool> IsPlatformSupported { get; set; } = OperatingSystem.IsWindows;

    /// <summary>Test seam: names of the local machine (NetBIOS name, DNS host name, real FQDN).</summary>
    internal Func<IReadOnlyCollection<string>> LocalHostNames { get; set; } = DefaultLocalHostNames;

    /// <summary>Account the PowerShell calls run as (named in error texts so it is clear who lacks rights).</summary>
    internal string ExecutingAccount { get; set; } = $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Number of task paths with a held or awaited lock (tests: must drop back to 0).</summary>
    internal int ActiveLockCount => _taskLocks.Count;

    internal ScheduledTaskAuditLog AuditLog => _audit;

    public ScheduledTaskControlService(ScheduledTaskControlOptions options, string auditDirectory, ILogger<ScheduledTaskControlService> logger)
        : this(options, auditDirectory, logger, new ScheduledTaskProcessRunner())
    {
    }

    internal ScheduledTaskControlService(
        ScheduledTaskControlOptions options, string auditDirectory, ILogger<ScheduledTaskControlService> logger, IScheduledTaskProcessRunner runner)
    {
        _options = options;
        _logger = logger;
        _runner = runner;
        _audit = new ScheduledTaskAuditLog(auditDirectory, options.EffectiveAuditHistorySize, logger);
    }

    public bool IsEnabled => _options.ControlEnabled;

    public string? DisabledReason => IsEnabled ? null : _options.DisabledReason ?? "Die Steuerung geplanter Aufgaben ist abgeschaltet.";

    private TimeSpan ExclusiveStepTimeout =>
        _options.EffectiveActionTimeout < ExclusiveStepTimeoutCap ? _options.EffectiveActionTimeout : ExclusiveStepTimeoutCap;

    // ── public API ─────────────────────────────────────────────────────────

    public async Task<ScheduledTaskActionResult> ExecuteAsync(
        string server, string taskPath, ScheduledTaskAction action, string user, CancellationToken ct = default)
    {
        server = (server ?? string.Empty).Trim();
        taskPath ??= string.Empty;
        if (!IsEnabled)
            return new ScheduledTaskActionResult(server, taskPath, action, false, DisabledReason!);
        if (!Enum.IsDefined(action))
            return new ScheduledTaskActionResult(server, taskPath, action, false, "Unbekannte Aktion.");

        var invalid = ValidateServer(server) ?? ValidateTaskPath(taskPath);
        if (invalid != null)
        {
            _logger.LogWarning("Scheduled task {Action} rejected for {User}: {Reason}", action, user, invalid);
            return new ScheduledTaskActionResult(server, taskPath, action, false, invalid);
        }

        // Enable/Disable change which server may run the task → path lock (shared with "activate exclusively").
        // Run/End only touch one server's instance → (server, path) lock. A call takes exactly one lock, so
        // there is no lock ordering and no deadlock.
        var lockKey = action is ScheduledTaskAction.Enable or ScheduledTaskAction.Disable ? PathLockKey(taskPath) : InstanceLockKey(server, taskPath);
        using var taskLock = await _taskLocks.TryAcquireAsync(lockKey, LockWait, ct).ConfigureAwait(false);
        if (taskLock == null)
        {
            // Nothing was executed — not audited.
            return new ScheduledTaskActionResult(server, taskPath, action, false, ct.IsCancellationRequested
                ? $"{Verb(action.ToString())} von „{taskPath}“ auf {server} abgebrochen — es wurde nichts ausgeführt."
                : BusyMessage(taskPath));
        }

        // From here on the change runs to completion (own timeout), even if the request is aborted.
        var outcome = await RunCoreAsync(server, taskPath, action.ToString(), _options.EffectiveActionTimeout, CancellationToken.None).ConfigureAwait(false);
        var result = new ScheduledTaskActionResult(server, taskPath, action, outcome.Success && !outcome.NotFound, outcome.Message);
        LogResult(result, user);

        var auditError = await _audit.AppendAsync(new[] { ToAudit(result, user) }).ConfigureAwait(false);
        return auditError == null ? result : result with { Message = result.Message + AuditNote(auditError) };
    }

    public async Task<ScheduledTaskExclusiveResult> ActivateExclusivelyAsync(
        string taskPath, string targetServer, IReadOnlyList<string> groupServers, string user,
        IReadOnlyCollection<string>? serversReportingTask = null, CancellationToken ct = default)
    {
        taskPath ??= string.Empty;
        targetServer = (targetServer ?? string.Empty).Trim();
        var noSteps = Array.Empty<ScheduledTaskActionResult>();
        if (!IsEnabled)
            return new ScheduledTaskExclusiveResult(taskPath, targetServer, false, noSteps, DisabledReason!);

        var group = (groupServers ?? Array.Empty<string>())
            .Select(s => (s ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalid = ValidateTaskPath(taskPath) ?? ValidateServer(targetServer)
                      ?? (group.Count == 0 ? "Die Servergruppe ist leer — es ist unklar, auf welchen Servern die Aufgabe deaktiviert werden soll." : null)
                      ?? group.Select(ValidateServer).FirstOrDefault(e => e != null);
        if (invalid != null)
        {
            _logger.LogWarning("Scheduled task exclusive activation rejected for {User}: {Reason}", user, invalid);
            return new ScheduledTaskExclusiveResult(taskPath, targetServer, false, noSteps, invalid);
        }
        if (!group.Contains(targetServer, StringComparer.OrdinalIgnoreCase))
            group.Add(targetServer);
        var others = group.Where(s => !s.Equals(targetServer, StringComparison.OrdinalIgnoreCase)).ToList();
        var reporting = new HashSet<string>(
            (serversReportingTask ?? Array.Empty<string>()).Select(s => (s ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);

        using var taskLock = await _taskLocks.TryAcquireAsync(PathLockKey(taskPath), LockWait, ct).ConfigureAwait(false);
        if (taskLock == null)
        {
            return new ScheduledTaskExclusiveResult(taskPath, targetServer, false, noSteps, ct.IsCancellationRequested
                ? $"Exklusive Aktivierung von „{taskPath}“ abgebrochen — es wurde nichts geändert."
                : BusyMessage(taskPath));
        }

        var stepTimeout = ExclusiveStepTimeout;
        var auditErrors = new List<string>();
        var steps = new List<ScheduledTaskActionResult>();
        string message;

        // 0. Check the target before touching anything (still cancellable — nothing changed yet).
        var check = await RunCoreAsync(targetServer, taskPath, QueryAction, stepTimeout, ct).ConfigureAwait(false);
        if (!check.Success || check.NotFound)
        {
            message = $"Exklusive Aktivierung von „{taskPath}“ auf {targetServer} nicht begonnen, es wurde nichts geändert: "
                      + (check.NotFound ? NotFoundMessage(targetServer, taskPath) : check.Message);
            return await FinishExclusiveAsync(taskPath, targetServer, user, false, steps, message, auditErrors).ConfigureAwait(false);
        }

        // 1. Commit phase — NOT cancellable: disable everywhere else (parallel), audited as one batch right after.
        var disableRuns = await Task.WhenAll(others.Select(async s =>
        {
            var o = await RunCoreAsync(s, taskPath, nameof(ScheduledTaskAction.Disable), stepTimeout, CancellationToken.None).ConfigureAwait(false);
            ScheduledTaskActionResult step;
            if (!o.NotFound)
                step = new ScheduledTaskActionResult(s, taskPath, ScheduledTaskAction.Disable, o.Success, o.Message);
            else if (reporting.Contains(s))
                step = new ScheduledTaskActionResult(s, taskPath, ScheduledTaskAction.Disable, false,
                    $"Der Worker meldet „{taskPath}“ auf {s}, aber das Konto {ExecutingAccount} findet die Aufgabe dort nicht (fehlende Leserechte auf den Aufgabenordner?) — sie kann nicht deaktiviert werden.");
            else
                step = new ScheduledTaskActionResult(s, taskPath, ScheduledTaskAction.Disable, true,
                    $"Aufgabe „{taskPath}“ ist auf {s} nicht vorhanden oder für das Konto {ExecutingAccount} nicht sichtbar — nichts zu deaktivieren.");
            LogResult(step, user);
            return (Step: step, NotFound: o.NotFound && step.Success, o.WasRunning);
        })).ConfigureAwait(false);
        steps.AddRange(disableRuns.Select(d => d.Step));
        if (steps.Count > 0)
            await AuditStepsAsync(steps, user, auditErrors).ConfigureAwait(false);

        var failed = disableRuns.Where(d => !d.Step.Success).Select(d => d.Step.Server).ToList();
        var disabled = disableRuns.Where(d => d.Step.Success && !d.NotFound).Select(d => d.Step.Server).ToList();
        var missing = disableRuns.Where(d => d.NotFound).Select(d => d.Step.Server).ToList();
        var stillRunning = disableRuns.Where(d => d.Step.Success && d.WasRunning).Select(d => d.Step.Server).ToList();

        if (failed.Count > 0)
        {
            // 2a. Do NOT enable the target — otherwise the task could run on two servers.
            message = $"„{taskPath}“ wurde auf {targetServer} NICHT aktiviert, weil die Deaktivierung auf {string.Join(", ", failed)} fehlgeschlagen ist (sonst könnte die Aufgabe doppelt laufen). "
                      + (disabled.Count > 0
                          ? $"Bereits deaktiviert: {string.Join(", ", disabled)} — die Aufgabe ist dort jetzt aus. "
                          : "Auf keinem anderen Server wurde etwas geändert. ")
                      + "Bitte die Fehler beheben und erneut ausführen.";
            return await FinishExclusiveAsync(taskPath, targetServer, user, false, steps, message, auditErrors).ConfigureAwait(false);
        }

        // 2b. All others are off — now enable the target.
        var e = await RunCoreAsync(targetServer, taskPath, nameof(ScheduledTaskAction.Enable), stepTimeout, CancellationToken.None).ConfigureAwait(false);
        var enable = new ScheduledTaskActionResult(targetServer, taskPath, ScheduledTaskAction.Enable, e.Success && !e.NotFound, e.Message);
        steps.Add(enable);
        LogResult(enable, user);
        await AuditStepsAsync(new[] { enable }, user, auditErrors).ConfigureAwait(false);

        var details = (disabled.Count > 0 ? $" Deaktiviert auf: {string.Join(", ", disabled)}." : string.Empty)
                      + (missing.Count > 0 ? $" Nicht vorhanden bzw. nicht sichtbar auf: {string.Join(", ", missing)}." : string.Empty)
                      + (stillRunning.Count > 0 ? $" Achtung, eine laufende Instanz wurde NICHT beendet auf: {string.Join(", ", stillRunning)}." : string.Empty);
        message = enable.Success
            ? $"„{taskPath}“ ist jetzt nur noch auf {targetServer} aktiv.{details}"
            : $"Alle anderen Server sind deaktiviert, aber die Aktivierung auf {targetServer} ist fehlgeschlagen: {enable.Message} Die Aufgabe ist derzeit auf KEINEM Server aktiv.{details}";
        return await FinishExclusiveAsync(taskPath, targetServer, user, enable.Success, steps, message, auditErrors).ConfigureAwait(false);
    }

    public async Task<ScheduledTaskLiveState> QueryAsync(string server, string taskPath, CancellationToken ct = default)
    {
        server = (server ?? string.Empty).Trim();
        taskPath ??= string.Empty;
        var invalid = ValidateServer(server) ?? ValidateTaskPath(taskPath);
        if (invalid != null)
            return new ScheduledTaskLiveState(server, false, null, invalid);

        var o = await RunCoreAsync(server, taskPath, QueryAction, _options.EffectiveActionTimeout, ct).ConfigureAwait(false);
        if (o.NotFound)
            return new ScheduledTaskLiveState(server, true, null, o.Message);
        return new ScheduledTaskLiveState(server, o.Reachable, o.Task, o.Success ? null : o.Message);
    }

    public IReadOnlyList<ScheduledTaskAuditEntry> GetAuditLog(int max = 200, string? taskPath = null) => _audit.Read(max, taskPath);

    private async Task<ScheduledTaskExclusiveResult> FinishExclusiveAsync(
        string taskPath, string targetServer, string user, bool success, List<ScheduledTaskActionResult> steps, string message, List<string> auditErrors)
    {
        _logger.Log(success ? LogLevel.Information : LogLevel.Warning,
            "Scheduled task {TaskPath} activate exclusively on {Server} by {User}: {Success} — {Message}",
            taskPath, targetServer, user, success, message);

        var summaryError = await _audit.AppendAsync(new[]
        {
            new ScheduledTaskAuditEntry
            {
                Timestamp = DateTime.UtcNow,
                User = NormalizeUser(user),
                Server = targetServer,
                TaskPath = taskPath,
                Action = ExclusiveAction,
                Success = success,
                Message = message
            }
        }).ConfigureAwait(false);
        if (summaryError != null) lock (auditErrors) auditErrors.Add(summaryError);

        if (auditErrors.Count > 0)
            message += AuditNote(auditErrors[0]);
        return new ScheduledTaskExclusiveResult(taskPath, targetServer, success, steps, message);
    }

    private static string PathLockKey(string taskPath) => "path|" + taskPath;

    private static string InstanceLockKey(string server, string taskPath) => "instance|" + server + "|" + taskPath;

    private async Task AuditStepsAsync(IReadOnlyCollection<ScheduledTaskActionResult> steps, string user, List<string> errors)
    {
        var error = await _audit.AppendAsync(steps.Select(s => ToAudit(s, user)).ToList()).ConfigureAwait(false);
        if (error != null) lock (errors) errors.Add(error);
    }

    // ── validation ─────────────────────────────────────────────────────────

    /// <summary>German error text, or null when <paramref name="server"/> is a plain host name (NetBIOS names may contain "_") / FQDN / IPv4.</summary>
    internal static string? ValidateServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
            return "Kein Server angegeben.";
        if (server.Length > 253 || !ServerPattern.IsMatch(server) || server.Contains("..", StringComparison.Ordinal))
            return "Ungültiger Servername — erlaubt sind nur Buchstaben, Ziffern, Punkt, Bindestrich und Unterstrich (max. 253 Zeichen).";
        return null;
    }

    /// <summary>German error text, or null when <paramref name="taskPath"/> is a full task path like "\Ordner\Name".</summary>
    internal static string? ValidateTaskPath(string? taskPath)
    {
        if (string.IsNullOrEmpty(taskPath))
            return "Kein Aufgabenpfad angegeben.";
        if (taskPath.Length > 260)
            return "Ungültiger Aufgabenpfad — maximal 260 Zeichen.";
        if (taskPath[0] != '\\')
            return "Ungültiger Aufgabenpfad — er muss mit „\\“ beginnen (z. B. „\\Ordner\\Aufgabe“).";
        if (taskPath.Any(char.IsControl))
            return "Ungültiger Aufgabenpfad — Steuerzeichen sind nicht erlaubt.";
        if (taskPath.Contains("..", StringComparison.Ordinal))
            return "Ungültiger Aufgabenpfad — „..“ ist nicht erlaubt.";
        if (taskPath.EndsWith('\\') || taskPath.Contains(@"\\", StringComparison.Ordinal))
            return "Ungültiger Aufgabenpfad — Ordner und Aufgabenname dürfen nicht leer sein.";
        // Characters Windows forbids in task names, plus the PowerShell/CIM wildcards (* ? [ ]) — a pattern
        // must never select more than the one task the user clicked.
        if (taskPath.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|', '[', ']' }) >= 0)
            return "Ungültiger Aufgabenpfad — die Zeichen / : * ? \" < > | [ ] sind nicht erlaubt.";
        return null;
    }

    /// <summary>Splits "\Ordner\Name" into TaskPath "\Ordner\" (with trailing backslash; root = "\") and TaskName "Name".</summary>
    internal static (string Folder, string Name) SplitTaskPath(string taskPath)
    {
        var idx = taskPath.LastIndexOf('\\');
        return (taskPath[..(idx + 1)], taskPath[(idx + 1)..]);
    }

    /// <summary>
    /// True when <paramref name="server"/> is exactly one of this machine's names (case-insensitive): the NetBIOS
    /// name, the DNS host name, the real FQDN (host name + primary DNS suffix) or "localhost"/127.0.0.1. Any other
    /// name — also "&lt;hostname&gt;.other.domain" — is treated as remote.
    /// </summary>
    internal static bool IsLocalServer(string server, IEnumerable<string> localNames) =>
        server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || server == "127.0.0.1"
        || localNames.Any(n => !string.IsNullOrWhiteSpace(n) && server.Equals(n, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyCollection<string> DefaultLocalHostNames()
    {
        var names = new List<string> { Environment.MachineName };
        try
        {
            var host = Dns.GetHostName();
            names.Add(host);
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(domain))
                names.Add(host + "." + domain);
        }
        catch
        {
            // Machine name alone is good enough.
        }
        return names;
    }

    // ── execution ──────────────────────────────────────────────────────────

    /// <summary>Outcome of one PowerShell call (before mapping to the public result types).</summary>
    internal sealed record CallOutcome(bool Success, bool Reachable, bool NotFound, ScheduledTaskInfo? Task, string Message, bool WasRunning = false);

    private async Task<CallOutcome> RunCoreAsync(string server, string taskPath, string action, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsPlatformSupported())
        {
            return new CallOutcome(false, false, false, null,
                $"Geplante Aufgaben können nur gesteuert werden, wenn die WebUI unter Windows läuft (aktuell: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}).");
        }

        var remote = !IsLocalServer(server, LocalHostNames());
        var script = BuildScript(server, taskPath, action, remote, timeout);
        var arguments = BuildArguments(script);

        ScheduledTaskProcessResult result;
        try
        {
            result = await _runner.RunAsync(_options.EffectivePowerShellPath, arguments, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only queries pass a cancellable token.
            return new CallOutcome(false, true, false, null, $"{Verb(action)} von „{taskPath}“ auf {server} abgebrochen.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Could not start PowerShell {Path} for scheduled task control", _options.EffectivePowerShellPath);
            return new CallOutcome(false, false, false, null,
                $"PowerShell („{_options.EffectivePowerShellPath}“) konnte nicht gestartet werden: {ex.Message} (Einstellung \"ScheduledTasks:PowerShellPath\").");
        }

        if (result.TimedOut)
        {
            return new CallOutcome(false, false, false, null,
                $"Zeitüberschreitung nach {timeout.TotalSeconds:0} s: {Verb(action)} von „{taskPath}“ auf {server} (ausgeführt als {ExecutingAccount}). "
                + "Ist der Server per RPC/DCOM erreichbar (Firewall)? Ob die Aktion ausgeführt wurde, ist unbekannt.");
        }

        return MapResult(server, taskPath, action, result);
    }

    internal CallOutcome MapResult(string server, string taskPath, string action, ScheduledTaskProcessResult process)
    {
        var parsed = ParseResultLine(process.StdOut, out var rawLine);
        if (parsed == null && rawLine != null)
            _logger.LogWarning("Unreadable scheduled task result from {Server}: {Line}", server, rawLine);

        if (parsed == null)
        {
            var detail = Shorten(string.IsNullOrWhiteSpace(process.StdErr) ? process.StdOut : process.StdErr);
            return new CallOutcome(false, false, false, null,
                $"{Verb(action)} von „{taskPath}“ auf {server} fehlgeschlagen (PowerShell-Exit-Code {process.ExitCode}, ausgeführt als {ExecutingAccount}): {detail}");
        }

        if (parsed.Ok)
        {
            if (!parsed.Found)
                return new CallOutcome(false, true, true, null, NotFoundMessage(server, taskPath));
            var wasRunning = parsed.WasRunning ?? false;
            var info = ToInfo(parsed.Task, taskPath);
            return new CallOutcome(true, true, false, info, SuccessMessage(server, taskPath, action, wasRunning), wasRunning);
        }

        var error = Shorten(parsed.Error ?? "Unbekannter Fehler");
        var code = string.IsNullOrWhiteSpace(parsed.Code) ? string.Empty : $" [{parsed.Code}]";
        var unreachable = parsed.Phase == "connect" || IsRpcError(parsed);
        var hint = IsAccessDenied(parsed)
            ? $" — Dem Konto {ExecutingAccount} fehlen auf {server} die Rechte (lokaler Administrator bzw. Vollzugriff auf den Aufgabenordner; remote zusätzlich DCOM-Remotezugriff und WMI-Rechte)."
            : unreachable
                ? $" — {server} ist per RPC/DCOM nicht erreichbar (Firewall-Regelgruppe „Remoteverwaltung geplanter Aufgaben“ bzw. „Remote Scheduled Tasks Management“ und „Windows-Verwaltungsinstrumentation (WMI)“ prüfen)."
                : string.Empty;
        return new CallOutcome(false, !unreachable, false, null,
            $"{Verb(action)} von „{taskPath}“ auf {server} fehlgeschlagen (ausgeführt als {ExecutingAccount}): {error}{code}{hint}");
    }

    /// <summary>Finds the last "SA-RESULT:&lt;base64&gt;" line and decodes the UTF-8 JSON (ASCII-safe under Windows PowerShell 5.1).</summary>
    private static ScriptResult? ParseResultLine(string stdout, out string? rawLine)
    {
        rawLine = stdout
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith(ResultMarker, StringComparison.Ordinal));
        if (rawLine == null) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(rawLine[ResultMarker.Length..]));
            return JsonSerializer.Deserialize<ScriptResult>(json, ResultJsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool IsAccessDenied(ScriptResult r) =>
        r.Category == "PermissionDenied"
        || string.Equals(r.Native, "AccessDenied", StringComparison.OrdinalIgnoreCase)
        || (r.Code?.Contains("80070005", StringComparison.OrdinalIgnoreCase) ?? false)
        || ContainsAny(r.Error, "Zugriff verweigert", "Access is denied", "Access denied");

    private static bool IsRpcError(ScriptResult r) =>
        (r.Code?.Contains("800706BA", StringComparison.OrdinalIgnoreCase) ?? false)
        || ContainsAny(r.Error, "RPC-Server ist nicht verfügbar", "RPC server is unavailable", "0x800706BA");

    private static bool ContainsAny(string? text, params string[] needles) =>
        text != null && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static ScheduledTaskInfo ToInfo(ScriptTask? t, string taskPath)
    {
        var (_, name) = SplitTaskPath(taskPath);
        var info = new ScheduledTaskInfo
        {
            Path = string.IsNullOrEmpty(t?.Path) ? taskPath : t!.Path!,
            Name = string.IsNullOrEmpty(t?.Name) ? name : t!.Name!,
            Description = string.IsNullOrWhiteSpace(t?.Description) ? null : t!.Description,
            Enabled = t?.Enabled ?? false,
            State = string.IsNullOrWhiteSpace(t?.State) ? "Unknown" : t!.State!,
            RunAs = string.IsNullOrWhiteSpace(t?.RunAs) ? null : t!.RunAs,
            LastRunTime = ParseUtc(t?.LastRunTime),
            NextRunTime = ParseUtc(t?.NextRunTime),
            // LastTaskResult is a uint32 (HRESULT); the model stores it as signed int like the checker.
            LastTaskResult = t?.LastTaskResult is long r ? unchecked((int)(uint)r) : null
        };
        if (info.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
            info.RunningSince = info.LastRunTime;
        return info;
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;

    // ── script ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the PowerShell script. Every caller-controlled value (server, task folder, task name) is embedded
    /// only as a <see cref="PowerShellLiteral.Quote"/> literal; <paramref name="action"/> comes from a fixed set.
    /// The last stdout line "SA-RESULT:&lt;Base64 of UTF-8 JSON&gt;" carries the result.
    /// </summary>
    internal static string BuildScript(string server, string taskPath, string action, bool remote, TimeSpan timeout)
    {
        var (folder, name) = SplitTaskPath(taskPath);
        var cimTimeout = Math.Max(5, (int)timeout.TotalSeconds - 5);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine($"$server = {PowerShellLiteral.Quote(server)}");
        sb.AppendLine($"$taskPath = {PowerShellLiteral.Quote(folder)}");
        sb.AppendLine($"$taskName = {PowerShellLiteral.Quote(name)}");
        sb.AppendLine($"$action = {PowerShellLiteral.Quote(action)}");
        sb.AppendLine($"$remote = {(remote ? "$true" : "$false")}");
        sb.AppendLine($"$cimTimeout = {cimTimeout.ToString(CultureInfo.InvariantCulture)}");
        sb.Append("""
            function ConvertTo-Utc($d) { if ($d -is [datetime] -and $d.Year -gt 2000) { $d.ToUniversalTime().ToString('o') } else { $null } }
            function New-Failure($phase, $err) {
                $ex = $err.Exception
                $code = $null; $native = $null
                try { $hr = $ex.ErrorData.CimInstanceProperties['error_Code'].Value; if ($null -ne $hr -and 0 -ne $hr) { $code = '0x{0:X8}' -f [uint32]$hr } } catch { }
                if ($null -eq $code -and $null -ne $ex) { $code = '0x{0:X8}' -f $ex.HResult }
                try { if ($null -ne $ex.NativeErrorCode) { $native = [string]$ex.NativeErrorCode } } catch { }
                @{ ok = $false; phase = $phase; category = [string]$err.CategoryInfo.Category; error = [string]$ex.Message; code = $code; native = $native }
            }
            function Find-Task($p) {
                @(Get-ScheduledTask @p -ErrorAction Stop | Where-Object { $_.TaskPath -eq $taskPath -and $_.TaskName -eq $taskName })
            }
            function Invoke-Main {
                $p = @{ TaskPath = $taskPath; TaskName = $taskName }
                if ($remote) {
                    try {
                        $opt = New-CimSessionOption -Protocol Dcom -ErrorAction Stop
                        $script:session = New-CimSession -ComputerName $server -SessionOption $opt -OperationTimeoutSec $cimTimeout -ErrorAction Stop
                        Get-CimInstance -CimSession $script:session -ClassName Win32_OperatingSystem -Property Caption -ErrorAction Stop | Out-Null
                        $p.CimSession = $script:session
                    } catch { return New-Failure 'connect' $_ }
                }
                try { $found = @(Find-Task $p) } catch {
                    if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return @{ ok = $true; found = $false } }
                    return New-Failure 'query' $_
                }
                if ($found.Count -eq 0) { return @{ ok = $true; found = $false } }
                if ($found.Count -gt 1) { return @{ ok = $false; phase = 'query'; category = 'Ambiguous'; error = 'Der Aufgabenpfad ist nicht eindeutig.' } }
                $wasRunning = ([string]$found[0].State -eq 'Running')
                try {
                    switch ($action) {
                        'Enable'  { Enable-ScheduledTask @p -ErrorAction Stop | Out-Null }
                        'Disable' { Disable-ScheduledTask @p -ErrorAction Stop | Out-Null }
                        'Run'     { Start-ScheduledTask @p -ErrorAction Stop | Out-Null }
                        'End'     { Stop-ScheduledTask @p -ErrorAction Stop | Out-Null }
                    }
                } catch { return New-Failure 'action' $_ }
                $t = $found[0]
                try { $again = @(Find-Task $p); if ($again.Count -eq 1) { $t = $again[0] } } catch { }
                $info = $null
                try { $info = Get-ScheduledTaskInfo @p -ErrorAction Stop } catch { }
                $runAs = $null
                if ($null -ne $t.Principal) { $runAs = $t.Principal.UserId; if (-not $runAs) { $runAs = $t.Principal.GroupId } }
                $last = $null; if ($null -ne $info -and $null -ne $info.LastTaskResult) { $last = [long]$info.LastTaskResult }
                @{ ok = $true; found = $true; wasRunning = $wasRunning; task = @{
                    path = [string]$t.TaskPath + [string]$t.TaskName; name = [string]$t.TaskName; description = [string]$t.Description
                    enabled = [bool]$t.Settings.Enabled; state = [string]$t.State; runAs = [string]$runAs
                    lastRunTime = $(if ($null -ne $info) { ConvertTo-Utc $info.LastRunTime } else { $null })
                    nextRunTime = $(if ($null -ne $info) { ConvertTo-Utc $info.NextRunTime } else { $null })
                    lastTaskResult = $last } }
            }
            $script:session = $null
            try { $result = @(Invoke-Main)[-1] } catch { $result = New-Failure 'script' $_ }
            finally { if ($null -ne $script:session) { Remove-CimSession -CimSession $script:session -ErrorAction SilentlyContinue } }
            $json = $result | ConvertTo-Json -Compress -Depth 4
            [Console]::Out.WriteLine('SA-RESULT:' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json)))

            """);
        return sb.ToString();
    }

    /// <summary>Fixed command line: no profile, no prompts, script only as Base64 (UTF-16LE) — nothing is parsed from argv.</summary>
    internal static IReadOnlyList<string> BuildArguments(string script) => new[]
    {
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text",
        "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
    };

    // ── texts / audit ──────────────────────────────────────────────────────

    private static string Verb(string action) => action switch
    {
        nameof(ScheduledTaskAction.Enable) => "Aktivieren",
        nameof(ScheduledTaskAction.Disable) => "Deaktivieren",
        nameof(ScheduledTaskAction.Run) => "Starten",
        nameof(ScheduledTaskAction.End) => "Beenden",
        _ => "Abfragen"
    };

    private static string SuccessMessage(string server, string taskPath, string action, bool wasRunning) => action switch
    {
        nameof(ScheduledTaskAction.Enable) => $"Aufgabe „{taskPath}“ auf {server} aktiviert.",
        nameof(ScheduledTaskAction.Disable) => wasRunning
            ? $"Aufgabe „{taskPath}“ auf {server} deaktiviert — die laufende Instanz wurde dadurch NICHT beendet."
            : $"Aufgabe „{taskPath}“ auf {server} deaktiviert.",
        nameof(ScheduledTaskAction.Run) => wasRunning
            ? $"Start von „{taskPath}“ auf {server} angefordert — es lief bereits eine Instanz (ob eine zweite startet, hängt von der Einstellung „Mehrere Instanzen“ der Aufgabe ab)."
            : $"Aufgabe „{taskPath}“ auf {server} gestartet.",
        nameof(ScheduledTaskAction.End) => wasRunning
            ? $"Laufende Instanz von „{taskPath}“ auf {server} beendet."
            : $"Aufgabe „{taskPath}“ auf {server} war nicht aktiv — es gab nichts zu beenden.",
        _ => $"Aufgabe „{taskPath}“ auf {server} abgefragt."
    };

    private static string NotFoundMessage(string server, string taskPath) =>
        $"Aufgabe „{taskPath}“ ist auf {server} nicht vorhanden (oder für das ausführende Konto nicht sichtbar).";

    private static string BusyMessage(string taskPath) =>
        $"Für „{taskPath}“ läuft gerade eine andere Aktion; es wurde nichts ausgeführt. Bitte in einigen Sekunden erneut versuchen.";

    private static string AuditNote(string error) =>
        $" (Hinweis: Der Eintrag im Audit-Log konnte nicht geschrieben werden: {error} — er wird beim nächsten Schreiben nachgeholt.)";

    private static string Shorten(string text)
    {
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        return t.Length <= 600 ? t : t[..600] + " …";
    }

    private static string NormalizeUser(string? user) => string.IsNullOrWhiteSpace(user) ? "(unbekannt)" : user.Trim();

    private static ScheduledTaskAuditEntry ToAudit(ScheduledTaskActionResult r, string user) => new()
    {
        Timestamp = DateTime.UtcNow,
        User = NormalizeUser(user),
        Server = r.Server,
        TaskPath = r.TaskPath,
        Action = r.Action.ToString(),
        Success = r.Success,
        Message = r.Message
    };

    private void LogResult(ScheduledTaskActionResult r, string user) =>
        _logger.Log(r.Success ? LogLevel.Information : LogLevel.Warning,
            "Scheduled task {Action} {TaskPath} on {Server} by {User}: {Success} — {Message}",
            r.Action, r.TaskPath, r.Server, user, r.Success, r.Message);

    // ── result DTOs (JSON from the script) ─────────────────────────────────

    private sealed class ScriptResult
    {
        public bool Ok { get; set; }
        public bool Found { get; set; }
        public bool? WasRunning { get; set; }
        public string? Phase { get; set; }
        public string? Category { get; set; }
        public string? Error { get; set; }
        public string? Code { get; set; }
        public string? Native { get; set; }
        public ScriptTask? Task { get; set; }
    }

    private sealed class ScriptTask
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool? Enabled { get; set; }
        public string? State { get; set; }
        public string? RunAs { get; set; }
        public string? LastRunTime { get; set; }
        public string? NextRunTime { get; set; }
        public long? LastTaskResult { get; set; }
    }
}
