using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Tests.Services;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class ScheduledTaskControlServiceTests : IDisposable
{
    private const string TaskPath = @"\Minicon\Export";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-stc-" + Guid.NewGuid().ToString("N"));

    public ScheduledTaskControlServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>Stub for the PowerShell process: decodes the script and lets the test decide the outcome.</summary>
    private sealed class FakeRunner : IScheduledTaskProcessRunner
    {
        public ConcurrentQueue<Call> Calls { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public Func<Call, Task<ScheduledTaskProcessResult>> Handler { get; set; } = _ => Task.FromResult(Ok());
        private int _running;
        public int MaxParallel;

        public async Task<ScheduledTaskProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            var call = new Call(fileName, arguments);
            Calls.Enqueue(call);
            var now = Interlocked.Increment(ref _running);
            InterlockedMax(ref MaxParallel, now);
            Events.Enqueue($"start:{call.Action}:{call.Server}");
            try
            {
                return await Handler(call);
            }
            finally
            {
                Events.Enqueue($"end:{call.Action}:{call.Server}");
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private sealed record Call(string FileName, IReadOnlyList<string> Arguments)
    {
        public string Script => Encoding.Unicode.GetString(Convert.FromBase64String(Arguments[^1]));
        public string Server => Var("server");
        public string Action => Var("action");
        public bool Remote => Script.Contains("$remote = $true", StringComparison.Ordinal);

        private string Var(string name) =>
            Regex.Match(Script, $@"^\${name} = '(.*)'\r?$", RegexOptions.Multiline).Groups[1].Value.Replace("''", "'");
    }

    private static ScheduledTaskProcessResult Ok(bool enabled = true, string state = "Ready", long? lastResult = 0, bool wasRunning = false) =>
        Result("{\"ok\":true,\"found\":true,\"wasRunning\":" + (wasRunning ? "true" : "false") + ",\"task\":{\"path\":\"" + TaskPath.Replace(@"\", @"\\") + "\",\"name\":\"Export\",\"enabled\":" + (enabled ? "true" : "false")
            + ",\"state\":\"" + state + "\",\"runAs\":\"SYSTEM\",\"lastRunTime\":\"2026-11-05T06:00:00.0000000Z\",\"nextRunTime\":null,\"lastTaskResult\":" + (lastResult?.ToString() ?? "null") + "}}");

    private static ScheduledTaskProcessResult NotFound() => Result("""{"ok":true,"found":false}""");

    private static ScheduledTaskProcessResult Failure(string phase, string error, string category = "NotSpecified", string? code = null) =>
        Result($$"""{"ok":false,"phase":"{{phase}}","category":"{{category}}","error":"{{error}}","code":{{(code == null ? "null" : "\"" + code + "\"")}}}""");

    private static ScheduledTaskProcessResult Result(string json) =>
        new(0, "WARNUNG: irgendwas\r\nSA-RESULT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\r\n", string.Empty, false);

    private ScheduledTaskControlService CreateService(FakeRunner runner, bool enabled = true, int historySize = 2000, string? auditDir = null)
    {
        var options = new ScheduledTaskControlOptions { ControlEnabled = enabled, AuditHistorySize = historySize, PowerShellPath = "powershell.exe" };
        if (!enabled) options.DisabledReason = "abgeschaltet (Test)";
        return new ScheduledTaskControlService(options, auditDir ?? _dir, NullLogger<ScheduledTaskControlService>.Instance, runner)
        {
            IsPlatformSupported = () => true,
            LocalHostNames = () => new[] { "WEBUI01", "webui01.corp.local" },
            ExecutingAccount = @"CORP\svc-simpleadmin"
        };
    }

    // ── validation ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("srv01;calc")]
    [InlineData("srv01' ; Remove-Item C:\\ -Recurse ; '")]
    [InlineData("$(Stop-Computer)")]
    [InlineData("-ComputerName")]
    [InlineData("srv 01")]
    [InlineData("srv01..corp")]
    [InlineData("srv01\nsrv02")]
    [InlineData("\\\\srv01")]
    public async Task Execute_rejects_malicious_server_without_starting_a_process(string server)
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner);

        var result = await sut.ExecuteAsync(server, TaskPath, ScheduledTaskAction.Run, "CORP\\alice");

        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
        runner.Calls.Should().BeEmpty();
        sut.GetAuditLog().Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_rejects_overlong_server_name()
    {
        var runner = new FakeRunner();
        var result = await CreateService(runner).ExecuteAsync(new string('a', 254), TaskPath, ScheduledTaskAction.Enable, "u");

        result.Success.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Minicon\\Export")]
    [InlineData("\\Minicon\\..\\Windows\\Defrag")]
    [InlineData("\\Minicon\\Export\r\nStop-Computer")]
    [InlineData("\\Minicon\\Exp\u0000ort")]
    [InlineData("\\Minicon\\*")]
    [InlineData("\\Minicon\\Exp?rt")]
    [InlineData("\\Minicon\\[E]xport")]
    [InlineData("\\Minicon\\")]
    [InlineData("\\\\Minicon\\Export")]
    [InlineData("\\Minicon\\Ex|port")]
    [InlineData("/Minicon/Export")]
    public async Task Execute_rejects_malicious_task_path_without_starting_a_process(string path)
    {
        var runner = new FakeRunner();
        var result = await CreateService(runner).ExecuteAsync("srv01", path, ScheduledTaskAction.Disable, "u");

        result.Success.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_rejects_overlong_task_path()
    {
        var runner = new FakeRunner();
        var result = await CreateService(runner).ExecuteAsync("srv01", "\\" + new string('a', 260), ScheduledTaskAction.Disable, "u");

        result.Success.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\Export", @"\", "Export")]
    [InlineData(@"\Minicon\Export", @"\Minicon\", "Export")]
    [InlineData(@"\A\B\C Task", @"\A\B\", "C Task")]
    public void SplitTaskPath_keeps_trailing_backslash_on_the_folder(string path, string folder, string name)
    {
        ScheduledTaskControlService.SplitTaskPath(path).Should().Be((folder, name));
    }

    // ── script hardening ───────────────────────────────────────────────────

    [Fact]
    public async Task Script_embeds_values_only_as_quoted_literals_and_runs_encoded()
    {
        var runner = new FakeRunner();
        const string path = "\\O'Brien\\Task‘ $(Stop-Computer) `whoami`";

        var result = await CreateService(runner).ExecuteAsync("srv01.corp.local", path, ScheduledTaskAction.Enable, "u");

        result.Success.Should().BeTrue();
        var call = runner.Calls.Single();
        call.FileName.Should().Be("powershell.exe");
        call.Arguments.Take(call.Arguments.Count - 1).Should().Equal(
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand");
        call.Arguments.Should().NotContain(a => a.Contains("Brien"));

        var script = call.Script;
        script.Should().Contain("$server = 'srv01.corp.local'");
        script.Should().Contain("$taskPath = '\\O''Brien\\'");
        script.Should().Contain("$taskName = 'Task‘‘ $(Stop-Computer) `whoami`'");
        script.Should().Contain("$action = 'Enable'");
        script.Should().Contain("New-CimSessionOption -Protocol Dcom");
        script.Should().Contain("Enable-ScheduledTask @p");
    }

    [Fact]
    public async Task Script_for_root_task_uses_backslash_as_task_path()
    {
        var runner = new FakeRunner();
        await CreateService(runner).ExecuteAsync("srv01", @"\Export", ScheduledTaskAction.Run, "u");

        runner.Calls.Single().Script.Should().Contain("$taskPath = '\\'").And.Contain("$taskName = 'Export'");
    }

    // ── local vs remote ────────────────────────────────────────────────────

    [Theory]
    [InlineData("WEBUI01", true)]
    [InlineData("webui01", true)]
    [InlineData("webui01.corp.local", true)]
    [InlineData("WEBUI01.other.domain", false)]
    [InlineData("webui01.corp", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("webui011", false)]
    [InlineData("srv01", false)]
    [InlineData("srv01.corp.local", false)]
    public void IsLocalServer_matches_only_the_exact_local_names(string server, bool expected)
    {
        ScheduledTaskControlService.IsLocalServer(server, new[] { "WEBUI01", "webui01.corp.local" }).Should().Be(expected);
    }

    [Fact]
    public async Task Local_server_runs_without_cim_session_remote_with_dcom_session()
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner);

        await sut.QueryAsync("webui01.corp.local", TaskPath);
        await sut.QueryAsync("srv02", TaskPath);

        var calls = runner.Calls.ToArray();
        calls[0].Remote.Should().BeFalse();
        calls[1].Remote.Should().BeTrue();
    }

    // ── gate ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_off_rejects_mutations_without_process_but_allows_query()
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner, enabled: false);

        sut.IsEnabled.Should().BeFalse();
        sut.DisabledReason.Should().Be("abgeschaltet (Test)");

        foreach (var action in Enum.GetValues<ScheduledTaskAction>())
        {
            var r = await sut.ExecuteAsync("srv01", TaskPath, action, "u");
            r.Success.Should().BeFalse();
            r.Message.Should().Be("abgeschaltet (Test)");
        }
        var ex = await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "u");
        ex.Success.Should().BeFalse();
        ex.Steps.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();

        var live = await sut.QueryAsync("srv01", TaskPath);
        live.Task.Should().NotBeNull();
        runner.Calls.Should().ContainSingle().Which.Action.Should().Be("Query");
    }

    [Fact]
    public void Options_are_forced_off_without_authentication_unless_allowed()
    {
        var off = ScheduledTaskControlOptions.Resolve(Config(), authenticationEnabled: false);
        off.ControlEnabled.Should().BeFalse();
        off.DisabledReason.Should().Contain("ohne Anmeldung").And.Contain("AllowWithoutAuthentication");

        var allowed = ScheduledTaskControlOptions.Resolve(Config(("ScheduledTasks:AllowWithoutAuthentication", "true")), authenticationEnabled: false);
        allowed.ControlEnabled.Should().BeTrue();
        allowed.DisabledReason.Should().BeNull();

        var optOut = ScheduledTaskControlOptions.Resolve(Config(("ScheduledTasks:ControlEnabled", "false")), authenticationEnabled: true);
        optOut.ControlEnabled.Should().BeFalse();
        optOut.DisabledReason.Should().Contain("ControlEnabled");

        var defaults = ScheduledTaskControlOptions.Resolve(Config(("ScheduledTasks:DisabledReason", "x")), authenticationEnabled: true);
        defaults.ControlEnabled.Should().BeTrue();
        defaults.DisabledReason.Should().BeNull("the reason is never bound from configuration");
        defaults.EffectiveActionTimeout.Should().Be(TimeSpan.FromSeconds(60));
        defaults.EffectiveAuditHistorySize.Should().Be(2000);

        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
    }

    [Fact]
    public async Task Non_windows_platform_returns_german_error_without_process()
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner);
        sut.IsPlatformSupported = () => false;

        var r = await sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("Windows");
        runner.Calls.Should().BeEmpty();
    }

    // ── results / errors ───────────────────────────────────────────────────

    [Fact]
    public async Task Query_maps_task_info_and_signed_last_result()
    {
        var runner = new FakeRunner { Handler = _ => Task.FromResult(Ok(state: "Running", lastResult: 2147942402)) };

        var live = await CreateService(runner).QueryAsync("srv01", TaskPath);

        live.Reachable.Should().BeTrue();
        live.Error.Should().BeNull();
        live.Task!.Path.Should().Be(TaskPath);
        live.Task.Enabled.Should().BeTrue();
        live.Task.LastTaskResult.Should().Be(unchecked((int)0x80070002));
        live.Task.LastRunTime.Should().Be(new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc));
        live.Task.LastRunTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        live.Task.RunningSince.Should().Be(live.Task.LastRunTime);
        live.Task.NextRunTime.Should().BeNull();
    }

    [Fact]
    public async Task Access_denied_names_server_and_executing_account()
    {
        var runner = new FakeRunner
        {
            Handler = _ => Task.FromResult(Failure("action", "Zugriff verweigert", "PermissionDenied", "0x80070005"))
        };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Disable, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("srv01").And.Contain(@"CORP\svc-simpleadmin").And.Contain("Rechte").And.Contain("0x80070005");
    }

    [Fact]
    public async Task Rpc_failure_marks_server_unreachable_with_firewall_hint()
    {
        var runner = new FakeRunner
        {
            Handler = _ => Task.FromResult(Failure("connect", "Der RPC-Server ist nicht verfügbar.", code: "0x800706BA"))
        };

        var live = await CreateService(runner).QueryAsync("srv01", TaskPath);

        live.Reachable.Should().BeFalse();
        live.Error.Should().Contain("Remote Scheduled Tasks Management").And.Contain(@"CORP\svc-simpleadmin");
    }

    [Fact]
    public async Task Missing_result_line_reports_exit_code_and_stderr()
    {
        var runner = new FakeRunner
        {
            Handler = _ => Task.FromResult(new ScheduledTaskProcessResult(1, "", "Get-ScheduledTask : Modul nicht gefunden", false))
        };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("Exit-Code 1").And.Contain("Modul nicht gefunden");
    }

    [Fact]
    public async Task Not_found_is_a_failure_for_single_actions()
    {
        var runner = new FakeRunner { Handler = _ => Task.FromResult(NotFound()) };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Enable, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("nicht vorhanden");
    }

    [Fact]
    public async Task Timeout_from_the_seam_is_reported_in_german()
    {
        var runner = new FakeRunner { Handler = _ => Task.FromResult(new ScheduledTaskProcessResult(-1, "", "", true)) };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.End, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("Zeitüberschreitung").And.Contain("srv01").And.Contain(@"CORP\svc-simpleadmin");
    }

    [UnixFact]
    public async Task Default_runner_kills_the_process_on_timeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new ScheduledTaskProcessRunner().RunAsync("/bin/sh", new[] { "-c", "echo before; sleep 30" }, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [UnixFact]
    public async Task Default_runner_reads_stdout_and_stderr()
    {
        var result = await new ScheduledTaskProcessRunner().RunAsync("/bin/sh", new[] { "-c", "echo out; echo err 1>&2; exit 3" }, TimeSpan.FromSeconds(10), CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(3);
        result.StdOut.Trim().Should().Be("out");
        result.StdErr.Trim().Should().Be("err");
    }

    [Fact]
    public async Task Actions_on_the_same_task_and_server_never_run_in_parallel()
    {
        var runner = new FakeRunner();
        runner.Handler = async _ => { await Task.Delay(50); return Ok(); };
        var sut = CreateService(runner);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u")));

        runner.Calls.Should().HaveCount(4);
        runner.MaxParallel.Should().Be(1);
    }

    [Fact]
    public async Task Enable_and_disable_share_the_path_lock_while_run_and_end_lock_per_server()
    {
        // Run/End wait for each other: they only complete when both are inside the runner at the same time,
        // so the parallelism check does not depend on Task.Delay timing (a serialized pair times out).
        var runEndStarted = 0;
        var bothRunEndStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner();
        runner.Handler = async c =>
        {
            if (c.Action is "Run" or "End")
            {
                if (Interlocked.Increment(ref runEndStarted) == 2)
                    bothRunEndStarted.TrySetResult();
                await bothRunEndStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                await Task.Delay(20);
            }
            return Ok();
        };
        var sut = CreateService(runner);

        await Task.WhenAll(
            sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Enable, "u"),
            sut.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.Disable, "u"));
        runner.MaxParallel.Should().Be(1, "enable/disable on any server of the same task are serialized");

        runner.MaxParallel = 0;
        await Task.WhenAll(
            sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u"),
            sut.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.End, "u"));
        runner.MaxParallel.Should().Be(2, "run/end on different servers do not block each other");

        sut.ActiveLockCount.Should().Be(0);
    }

    // ── activate exclusively ───────────────────────────────────────────────

    [Fact]
    public async Task Exclusive_disables_all_others_before_enabling_the_target()
    {
        var runner = new FakeRunner();
        runner.Handler = async c => { await Task.Delay(c.Server == "srv03" ? 80 : 10); return Ok(); };
        var sut = CreateService(runner);

        var r = await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "CORP\\alice");

        r.Success.Should().BeTrue(r.Message);
        r.Steps.Select(s => (s.Server, s.Action)).Should().BeEquivalentTo(new[]
        {
            ("srv02", ScheduledTaskAction.Disable), ("srv03", ScheduledTaskAction.Disable), ("srv01", ScheduledTaskAction.Enable)
        });
        r.Steps[^1].Server.Should().Be("srv01");

        var events = runner.Events.ToList();
        var enableStart = events.IndexOf("start:Enable:srv01");
        enableStart.Should().BeGreaterThan(events.IndexOf("end:Disable:srv02"));
        enableStart.Should().BeGreaterThan(events.IndexOf("end:Disable:srv03"));
        runner.Calls.Count(c => c.Action == "Enable").Should().Be(1);
    }

    [Fact]
    public async Task Exclusive_does_not_enable_target_when_a_disable_failed()
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Server == "srv03"
                ? Failure("connect", "Der RPC-Server ist nicht verfügbar.", code: "0x800706BA")
                : Ok(enabled: false))
        };
        var sut = CreateService(runner);

        var r = await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "u");

        r.Success.Should().BeFalse();
        runner.Calls.Should().NotContain(c => c.Action == "Enable");
        r.Steps.Should().HaveCount(2);
        r.Message.Should().Contain("NICHT aktiviert").And.Contain("srv03").And.Contain("Bereits deaktiviert: srv02");
    }

    [Fact]
    public async Task Exclusive_treats_missing_task_on_other_server_as_ok()
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Server == "srv02" ? NotFound() : Ok())
        };

        var r = await CreateService(runner).ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "u");

        r.Success.Should().BeTrue(r.Message);
        r.Steps.Single(s => s.Server == "srv02").Success.Should().BeTrue();
        r.Message.Should().Contain("Nicht vorhanden bzw. nicht sichtbar auf: srv02").And.Contain("Deaktiviert auf: srv03");
        runner.Calls.Should().ContainSingle(c => c.Action == "Enable" && c.Server == "srv01");
    }

    [Theory]
    [InlineData("notfound")]
    [InlineData("unreachable")]
    [InlineData("error")]
    public async Task Exclusive_checks_the_target_first_and_changes_nothing_when_it_fails(string kind)
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Server != "srv01" ? Ok() : kind switch
            {
                "notfound" => NotFound(),
                "unreachable" => Failure("connect", "Der RPC-Server ist nicht verfügbar.", code: "0x800706BA"),
                _ => Failure("query", "Zugriff verweigert", "PermissionDenied", "0x80070005")
            })
        };
        var sut = CreateService(runner);

        var r = await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "u");

        r.Success.Should().BeFalse();
        r.Steps.Should().BeEmpty();
        r.Message.Should().Contain("nichts geändert");
        runner.Calls.Should().ContainSingle().Which.Action.Should().Be("Query");
        sut.GetAuditLog().Should().ContainSingle().Which.Action.Should().Be("ActivateExclusively");
    }

    [Fact]
    public async Task Exclusive_reports_no_active_server_when_final_enable_fails()
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Action == "Enable" ? Failure("action", "Unerwarteter Fehler") : Ok())
        };

        var r = await CreateService(runner).ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "u");

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("KEINEM Server");
    }

    [Fact]
    public async Task Exclusive_cancelled_during_disable_still_enables_the_target()
    {
        using var cts = new CancellationTokenSource();
        var disableStarted = new TaskCompletionSource();
        var runner = new FakeRunner();
        runner.Handler = async c =>
        {
            if (c.Action == "Disable")
            {
                disableStarted.TrySetResult();
                await Task.Delay(150);
            }
            return Ok();
        };
        var sut = CreateService(runner);

        var pending = sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "u", ct: cts.Token);
        await disableStarted.Task;
        cts.Cancel();
        var r = await pending;

        r.Success.Should().BeTrue(r.Message);
        runner.Calls.Should().Contain(c => c.Action == "Enable" && c.Server == "srv01");
    }

    [Fact]
    public async Task Exclusive_cancelled_before_the_lock_changes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new FakeRunner();

        var r = await CreateService(runner).ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "u", ct: cts.Token);

        r.Success.Should().BeFalse();
        r.Message.Should().Contain("abgebrochen");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Exclusive_and_concurrent_single_enable_on_the_same_task_are_serialized()
    {
        var runner = new FakeRunner();
        runner.Handler = async _ => { await Task.Delay(40); return Ok(); };
        var sut = CreateService(runner);

        var exclusive = sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "u");
        var single = sut.ExecuteAsync("srv02", TaskPath.ToUpperInvariant(), ScheduledTaskAction.Enable, "u");
        await Task.WhenAll(exclusive, single);

        runner.MaxParallel.Should().Be(1);
        var events = runner.Events.ToList();
        var singleStart = events.IndexOf("start:Enable:srv02");
        var exclusiveFirst = events.IndexOf("start:Query:srv01");
        var exclusiveLast = events.LastIndexOf("end:Enable:srv01");
        (singleStart < exclusiveFirst || singleStart > exclusiveLast).Should().BeTrue(string.Join(", ", events));
        sut.ActiveLockCount.Should().Be(0);
    }

    [Fact]
    public async Task Exclusive_treats_not_found_as_error_where_the_worker_reports_the_task()
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Server is "srv02" or "srv03" && c.Action == "Disable" ? NotFound() : Ok())
        };

        var r = await CreateService(runner).ActivateExclusivelyAsync(
            TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "u", serversReportingTask: new[] { "SRV02", "srv01" });

        r.Success.Should().BeFalse();
        r.Steps.Single(s => s.Server == "srv02").Message.Should().Contain("Worker meldet").And.Contain(@"CORP\svc-simpleadmin");
        r.Steps.Single(s => s.Server == "srv03").Success.Should().BeTrue();
        runner.Calls.Should().NotContain(c => c.Action == "Enable");
    }

    [Fact]
    public async Task Exclusive_success_names_instances_still_running_on_disabled_servers()
    {
        var runner = new FakeRunner
        {
            Handler = c => Task.FromResult(c.Action == "Disable" && c.Server == "srv02" ? Ok(enabled: false, state: "Running", wasRunning: true) : Ok())
        };

        var r = await CreateService(runner).ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02", "srv03" }, "u");

        r.Success.Should().BeTrue();
        r.Message.Should().Contain("NICHT beendet auf: srv02");
    }

    [Theory]
    [InlineData(true, "beendet")]
    [InlineData(false, "war nicht aktiv")]
    public async Task End_reports_whether_an_instance_was_running(bool wasRunning, string expected)
    {
        var runner = new FakeRunner { Handler = _ => Task.FromResult(Ok(wasRunning: wasRunning)) };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.End, "u");

        r.Success.Should().BeTrue();
        r.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task Single_action_runs_to_completion_when_cancelled_after_the_lock()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner();
        runner.Handler = async _ => { cts.Cancel(); await Task.Delay(30); return Ok(); };

        var r = await CreateService(runner).ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Disable, "u", cts.Token);

        r.Success.Should().BeTrue(r.Message);
    }

    [Fact]
    public async Task Exclusive_adds_target_to_group_and_rejects_empty_or_invalid_group()
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner);

        var added = await sut.ActivateExclusivelyAsync(TaskPath, "SRV01", new[] { "srv02" }, "u");
        added.Success.Should().BeTrue();
        added.Steps.Should().HaveCount(2);

        var sameNameDifferentCase = await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "SRV01", "srv02" }, "u");
        sameNameDifferentCase.Steps.Should().HaveCount(2, "the target is never disabled");

        runner.Calls.Clear();
        (await sut.ActivateExclusivelyAsync(TaskPath, "srv01", Array.Empty<string>(), "u")).Message.Should().Contain("leer");
        (await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv02", "srv03;calc" }, "u")).Success.Should().BeFalse();
        (await sut.ActivateExclusivelyAsync(@"\Minicon\*", "srv01", new[] { "srv02" }, "u")).Success.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    // ── audit log ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_action_is_audited_and_exclusive_adds_a_summary_entry()
    {
        var runner = new FakeRunner();
        var sut = CreateService(runner);

        await sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "CORP\\alice");
        await sut.ActivateExclusivelyAsync(TaskPath, "srv01", new[] { "srv01", "srv02" }, "CORP\\bob");

        var log = sut.GetAuditLog();
        log.Should().HaveCount(4);
        log.Select(e => e.Action).Should().BeEquivalentTo(new[] { "Run", "Disable", "Enable", "ActivateExclusively" });
        log.Should().OnlyContain(e => e.Success && e.TaskPath == TaskPath);
        log.Single(e => e.Action == "Run").User.Should().Be("CORP\\alice");
        log.Single(e => e.Action == "ActivateExclusively").Server.Should().Be("srv01");
        log.Should().BeInDescendingOrder(e => e.Timestamp);
        File.Exists(Path.Combine(_dir, "scheduled-task-actions.json")).Should().BeTrue();
        Directory.GetFiles(_dir).Should().NotContain(f => f.EndsWith(".tmp") || f.EndsWith(".lock"));
    }

    [Fact]
    public async Task Two_instances_on_the_same_directory_merge_their_audit_entries()
    {
        var a = CreateService(new FakeRunner());
        var b = CreateService(new FakeRunner());

        await a.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Enable, "alice");
        await b.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.Disable, "bob");
        await a.ExecuteAsync("srv01", @"\Other", ScheduledTaskAction.Run, "alice");

        var fresh = CreateService(new FakeRunner());
        fresh.GetAuditLog().Should().HaveCount(3);
        fresh.GetAuditLog(taskPath: TaskPath.ToUpperInvariant()).Select(e => e.User).Should().BeEquivalentTo(new[] { "alice", "bob" });
        fresh.GetAuditLog(max: 1).Single().TaskPath.Should().Be(@"\Other");
    }

    [Fact]
    public async Task Audit_log_is_capped_to_history_size()
    {
        var sut = CreateService(new FakeRunner(), historySize: 10);

        for (var i = 0; i < 15; i++)
            await sut.ExecuteAsync("srv01", $@"\T{i:00}", ScheduledTaskAction.Run, "u");

        var log = CreateService(new FakeRunner(), historySize: 10).GetAuditLog(1000);
        log.Should().HaveCount(10);
        log[0].TaskPath.Should().Be(@"\T14");
        log[^1].TaskPath.Should().Be(@"\T05");
    }

    [Fact]
    public async Task Audit_write_failure_does_not_fail_the_action_and_is_noted()
    {
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var sut = CreateService(new FakeRunner(), auditDir: blocker);

        var r = await sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u");

        r.Success.Should().BeTrue();
        r.Message.Should().Contain("Audit-Log");
        sut.GetAuditLog().Should().ContainSingle("unwritten entries stay pending in memory");
    }

    [Fact]
    public async Task Corrupt_audit_file_is_moved_aside_not_overwritten()
    {
        var file = Path.Combine(_dir, "scheduled-task-actions.json");
        await File.WriteAllTextAsync(file, "{ kaputt");
        var sut = CreateService(new FakeRunner());

        await sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u");

        sut.GetAuditLog().Should().ContainSingle();
        Directory.GetFiles(_dir, "scheduled-task-actions.json.corrupt-*").Should().ContainSingle()
            .Which.Should().Match(f => File.ReadAllText(f) == "{ kaputt");
    }

    [Fact]
    public async Task Audit_is_not_written_without_the_file_lock_and_stays_pending()
    {
        var sut = CreateService(new FakeRunner());
        sut.AuditLog.LockAttempts = 2;
        var lockPath = Path.Combine(_dir, "scheduled-task-actions.json.lock");

        await using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var r = await sut.ExecuteAsync("srv01", TaskPath, ScheduledTaskAction.Run, "u");
            r.Success.Should().BeTrue();
            r.Message.Should().Contain("Audit-Log");
            File.Exists(Path.Combine(_dir, "scheduled-task-actions.json")).Should().BeFalse();
            sut.GetAuditLog().Should().ContainSingle();
        }
        File.Delete(lockPath);

        await sut.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.Run, "u");
        CreateService(new FakeRunner()).GetAuditLog().Should().HaveCount(2, "the pending entry is written with the next append");
    }

    // ── PowerShell script (runs only with pwsh, see PowerShellFactAttribute) ─

    /// <summary>Stubs replace the ScheduledTasks/CIM cmdlets (functions win over cmdlets) so the real script logic runs.</summary>
    private const string Stubs = """
        function New-CimSessionOption { [CmdletBinding()] param($Protocol) [pscustomobject]@{ Protocol = $Protocol } }
        function New-CimSession { [CmdletBinding()] param($ComputerName, $SessionOption, $OperationTimeoutSec)
            if ($env:SA_STUB_CONNECT -eq 'fail') { Write-Error -Message 'Der RPC-Server ist nicht verfügbar.' -Category ConnectionError; return }
            [pscustomobject]@{ Id = 1; ComputerName = $ComputerName } }
        function Get-CimInstance { [CmdletBinding()] param($CimSession, $ClassName, $Property) [pscustomobject]@{ Caption = 'Windows' } }
        function Remove-CimSession { [CmdletBinding()] param($CimSession) Write-Output 'STUB-REMOVED' }
        function Get-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession)
            if ($env:SA_STUB_TASK -eq 'missing') { Write-Error -Message 'No MSFT_ScheduledTask objects found' -Category ObjectNotFound; return }
            [pscustomobject]@{ TaskPath = $TaskPath; TaskName = $TaskName; State = 'Running'; Description = 'Ä-Test'
                Settings = [pscustomobject]@{ Enabled = $true }; Principal = [pscustomobject]@{ UserId = 'SYSTEM'; GroupId = $null } } }
        function Get-ScheduledTaskInfo { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession)
            [pscustomobject]@{ LastRunTime = [datetime]::new(2026, 11, 5, 6, 0, 0, [DateTimeKind]::Utc); NextRunTime = [datetime]::new(1999, 11, 30); LastTaskResult = [uint32]2147942402 } }
        function Stop-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession)
            if ($env:SA_STUB_ACTION -eq 'fail') { Write-Error -Message 'Zugriff verweigert' -Category PermissionDenied; return }
            Write-Output 'STUB-STOPPED' }
        function Enable-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession) }
        function Disable-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession) }
        function Start-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName, $CimSession) }

        """;

    private async Task<(ScheduledTaskProcessResult Process, ScheduledTaskControlService.CallOutcome Outcome)> RunStubbedAsync(
        bool remote, string connect = "ok", string task = "ok", string action = "ok")
    {
        var script = Stubs.Replace("SA_STUB_CONNECT -eq 'fail'", connect == "fail" ? "SA_STUB_CONNECT -ne 'x'" : "SA_STUB_CONNECT -eq 'fail'")
                          .Replace("SA_STUB_TASK -eq 'missing'", task == "missing" ? "SA_STUB_TASK -ne 'x'" : "SA_STUB_TASK -eq 'missing'")
                          .Replace("SA_STUB_ACTION -eq 'fail'", action == "fail" ? "SA_STUB_ACTION -ne 'x'" : "SA_STUB_ACTION -eq 'fail'")
                     + ScheduledTaskControlService.BuildScript("srv01", @"\Mini'con\Export", "End", remote, TimeSpan.FromSeconds(30));
        var process = await new ScheduledTaskProcessRunner().RunAsync(
            TestPowerShell.Path!, ScheduledTaskControlService.BuildArguments(script), TimeSpan.FromSeconds(60), CancellationToken.None);
        var outcome = CreateService(new FakeRunner()).MapResult("srv01", @"\Mini'con\Export", "End", process);
        return (process, outcome);
    }

    [PowerShellFact]
    public async Task Script_parses_without_errors()
    {
        var file = Path.Combine(_dir, "script.ps1");
        await File.WriteAllTextAsync(file, ScheduledTaskControlService.BuildScript("srv01", @"\O'Brien\Task‘x", "Disable", true, TimeSpan.FromSeconds(60)), new UTF8Encoding(true));

        var check = $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{file.Replace("'", "''")}', [ref]$null, [ref]$e); Write-Output (\"ERRORS=\" + $e.Count); $e | ForEach-Object {{ Write-Output $_.Message }}";
        var r = await new ScheduledTaskProcessRunner().RunAsync(TestPowerShell.Path!, new[] { "-NoProfile", "-NonInteractive", "-Command", check }, TimeSpan.FromSeconds(60), CancellationToken.None);

        r.StdOut.Should().Contain("ERRORS=0", r.StdOut + r.StdErr);
    }

    [PowerShellFact]
    public async Task Script_with_stubs_reports_task_and_removes_the_session()
    {
        var (process, outcome) = await RunStubbedAsync(remote: true);

        outcome.Success.Should().BeTrue(process.StdOut + process.StdErr);
        outcome.WasRunning.Should().BeTrue();
        outcome.Message.Should().Contain("beendet");
        outcome.Task!.Path.Should().Be(@"\Mini'con\Export");
        outcome.Task.Description.Should().Be("Ä-Test");
        outcome.Task.LastTaskResult.Should().Be(unchecked((int)0x80070002));
        outcome.Task.LastRunTime.Should().Be(new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc));
        outcome.Task.NextRunTime.Should().BeNull();
        process.StdOut.Should().Contain("STUB-REMOVED");
    }

    [PowerShellFact]
    public async Task Script_with_stubs_maps_not_found()
    {
        var (process, outcome) = await RunStubbedAsync(remote: false, task: "missing");

        outcome.NotFound.Should().BeTrue(process.StdOut + process.StdErr);
        process.StdOut.Should().NotContain("STUB-REMOVED", "local calls use no CIM session");
    }

    [PowerShellFact]
    public async Task Script_with_stubs_maps_action_failure_and_still_removes_the_session()
    {
        var (process, outcome) = await RunStubbedAsync(remote: true, action: "fail");

        outcome.Success.Should().BeFalse();
        outcome.Reachable.Should().BeTrue();
        outcome.Message.Should().Contain("Zugriff verweigert").And.Contain("Rechte");
        process.StdOut.Should().Contain("STUB-REMOVED");
    }

    [PowerShellFact]
    public async Task Script_with_stubs_maps_connect_failure_as_unreachable()
    {
        var (process, outcome) = await RunStubbedAsync(remote: true, connect: "fail");

        outcome.Success.Should().BeFalse();
        outcome.Reachable.Should().BeFalse(process.StdOut + process.StdErr);
        outcome.Message.Should().Contain("RPC");
    }
}
