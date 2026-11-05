using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ScheduledTaskCheckerTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduledTaskWatchConfig Watch(string pattern, params string[] excludes)
        => new() { Pattern = pattern, ExcludePatterns = excludes.ToList() };

    private static ScheduledTaskInfo TaskInfo(string state = "Ready", bool enabled = true, DateTime? lastRun = null,
        int? result = null, DateTime? nextRun = null)
        => new() { Path = @"\Minicon\Export", Name = "Export", State = state, Enabled = enabled,
                   LastRunTime = lastRun, LastTaskResult = result, NextRunTime = nextRun };

    private static ScheduledTaskInfo Evaluate(ScheduledTaskInfo info, ScheduledTaskWatchConfig? watch = null, ScheduledTaskDefaults? defaults = null)
    {
        ScheduledTaskChecker.EvaluateTask(info, watch ?? Watch(@"\Minicon\*"), defaults ?? new ScheduledTaskDefaults(), Now);
        return info;
    }

    // ----- Pattern matching -----------------------------------------------------------------------

    [Theory]
    [InlineData(@"\Minicon\Export", @"\Minicon\*", true)]
    [InlineData(@"\MINICON\export", @"\minicon\*", true)]
    [InlineData(@"\Minicon\Sub\Export", @"\Minicon\*", true)]
    [InlineData(@"\Other\Export", @"\Minicon\*", false)]
    [InlineData(@"\Export1", @"\Export?", true)]
    [InlineData(@"\Export12", @"\Export?", false)]
    [InlineData(@"\Minicon\Export", @"Minicon\*", true)]
    [InlineData(@"\Minicon\Export", @"\Minicon\Export", true)]
    public void FindWatch_MatchesWildcardOnFullPath(string path, string pattern, bool expected)
        => (ScheduledTaskChecker.FindWatch(path, new[] { Watch(pattern) }) != null).Should().Be(expected);

    [Theory]
    [InlineData(@"\*")]
    [InlineData(@"*Defrag*")]
    [InlineData(@"\Micro*")]
    public void FindWatch_MicrosoftTasks_OnlyWhenPatternStartsWithMicrosoftFolder(string pattern)
        => ScheduledTaskChecker.FindWatch(@"\Microsoft\Windows\Defrag\ScheduledDefrag", new[] { Watch(pattern) })
            .Should().BeNull();

    [Fact]
    public void FindWatch_MicrosoftTasks_SelectedByExplicitMicrosoftPattern()
        => ScheduledTaskChecker.FindWatch(@"\Microsoft\Windows\Defrag\ScheduledDefrag", new[] { Watch(@"\microsoft\Windows\Defrag\*") })
            .Should().NotBeNull();

    [Fact]
    public void FindWatch_ExcludeOfFirstMatchingPatternIsFinal()
    {
        var general = Watch(@"\Minicon\*", @"Minicon\Special*");
        var special = Watch(@"\Minicon\Special*");

        ScheduledTaskChecker.FindWatch(@"\Minicon\SpecialJob", new[] { general }).Should().BeNull();
        ScheduledTaskChecker.FindWatch(@"\Minicon\SpecialJob", new[] { general, special }).Should().BeNull(
            "the first include match decides; its exclude is final");
        ScheduledTaskChecker.FindWatch(@"\Minicon\SpecialJob", new[] { special, general }).Should().BeSameAs(special);
        ScheduledTaskChecker.FindWatch(@"\Minicon\Export", new[] { general, special }).Should().BeSameAs(general);
    }

    [Fact]
    public void FindWatch_NormalizesTaskPathWithoutLeadingBackslash()
        => ScheduledTaskChecker.FindWatch(@"Minicon\Export", new[] { Watch(@"\Minicon\*") }).Should().NotBeNull();

    [Fact]
    public void FindWatch_FirstMatchingPatternWins()
    {
        var first = Watch(@"\Minicon\*");
        var second = Watch(@"\Minicon\Export");
        ScheduledTaskChecker.FindWatch(@"\Minicon\Export", new[] { first, second }).Should().BeSameAs(first);
    }

    [Fact]
    public void FindWatch_EmptyPatternIsIgnored()
        => ScheduledTaskChecker.FindWatch(@"\Minicon\Export", new[] { Watch("  ") }).Should().BeNull();

    [Fact]
    public void BuildPathPrefixes_UsesLiteralPartBeforeFirstWildcard()
        => ScheduledTaskChecker.BuildPathPrefixes(new[] { Watch(@"\Minicon\*"), Watch(@"\minicon\*"), Watch(@"\Export?x"), Watch("Job"), Watch("") })
            .Should().Equal(@"\Minicon\", @"\Export", @"\Job");

    // ----- Result codes ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(0x41300)]
    [InlineData(0x41301)]
    [InlineData(0x41302)]
    [InlineData(0x41303)]
    [InlineData(0x41304)]
    [InlineData(0x41305)]
    [InlineData(0x41306)]
    [InlineData(0x41307)]
    [InlineData(0x41308)]
    [InlineData(0x4131B)]
    [InlineData(0x41325)]
    [InlineData(unchecked((int)0x8004131F))]
    public void IsSuccessResult_InformationalCodes(int code)
        => ScheduledTaskChecker.IsSuccessResult(code).Should().BeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0x4131C)]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x800710E0))]
    [InlineData(unchecked((int)0xC000013A))]
    public void IsSuccessResult_FailureCodes(int code)
        => ScheduledTaskChecker.IsSuccessResult(code).Should().BeFalse();

    [Fact]
    public void IsSuccessResult_GlobalAndPatternAllowedCodes()
    {
        ScheduledTaskChecker.IsSuccessResult(2, new[] { 2 }).Should().BeTrue();
        ScheduledTaskChecker.IsSuccessResult(3, new[] { 2 }, new[] { 3 }).Should().BeTrue();
        ScheduledTaskChecker.IsSuccessResult(4, new[] { 2 }, new[] { 3 }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateTask_FailedLastRun_IsFlaggedWithSeverity()
    {
        var watch = Watch(@"\Minicon\*");
        watch.FailureIsCritical = false;
        var info = Evaluate(TaskInfo(lastRun: Now.AddHours(-1), result: 1), watch);
        info.LastRunFailed.Should().BeTrue();
        info.FailureIsCritical.Should().BeFalse();
        info.MatchedPattern.Should().Be(@"\Minicon\*");
    }

    [Fact]
    public void EvaluateTask_AllowedCodesFromDefaultsAndWatch()
    {
        var defaults = new ScheduledTaskDefaults { AllowedResultCodes = { 1 } };
        var watch = Watch(@"\Minicon\*");
        watch.AllowedResultCodes.Add(2);
        Evaluate(TaskInfo(lastRun: Now.AddHours(-1), result: 1), watch, defaults).LastRunFailed.Should().BeFalse();
        Evaluate(TaskInfo(lastRun: Now.AddHours(-1), result: 2), watch, defaults).LastRunFailed.Should().BeFalse();
        Evaluate(TaskInfo(lastRun: Now.AddHours(-1), result: 3), watch, defaults).LastRunFailed.Should().BeTrue();
    }

    [Fact]
    public void EvaluateTask_RunningOrNeverRun_HasNoFailure()
    {
        Evaluate(TaskInfo(state: "Running", lastRun: Now.AddMinutes(-5), result: 1)).LastRunFailed.Should().BeFalse();
        Evaluate(TaskInfo(lastRun: null, result: 1)).LastRunFailed.Should().BeFalse();
        Evaluate(TaskInfo(lastRun: Now.AddHours(-1), result: null)).LastRunFailed.Should().BeFalse();
    }

    // ----- Runtime --------------------------------------------------------------------------------

    [Fact]
    public void EvaluateTask_Running_SetsRunningSinceAndRuntime()
    {
        var info = Evaluate(TaskInfo(state: "Running", lastRun: Now.AddMinutes(-121)));
        info.RunningSince.Should().Be(Now.AddMinutes(-121));
        info.MaxRuntimeMinutes.Should().Be(120);
        info.RuntimeExceeded.Should().BeTrue();

        Evaluate(TaskInfo(state: "Running", lastRun: Now.AddMinutes(-119))).RuntimeExceeded.Should().BeFalse();
    }

    [Fact]
    public void EvaluateTask_RuntimeOverrideAndOff()
    {
        var watch = Watch(@"\Minicon\*");
        watch.MaxRuntimeMinutes = 10;
        Evaluate(TaskInfo(state: "Running", lastRun: Now.AddMinutes(-11)), watch).RuntimeExceeded.Should().BeTrue();

        watch.MaxRuntimeMinutes = 0;
        var off = Evaluate(TaskInfo(state: "Running", lastRun: Now.AddDays(-3)), watch);
        off.RuntimeExceeded.Should().BeFalse();
        off.MaxRuntimeMinutes.Should().Be(0);
    }

    [Fact]
    public void EvaluateTask_NotRunning_HasNoRunningSince()
    {
        var info = Evaluate(TaskInfo(state: "Ready", lastRun: Now.AddDays(-1)));
        info.RunningSince.Should().BeNull();
        info.RuntimeExceeded.Should().BeFalse();
    }

    // ----- Missed run -----------------------------------------------------------------------------

    [Fact]
    public void MissedRun_OutsideGrace_IsFlagged()
        => Evaluate(TaskInfo(nextRun: Now.AddMinutes(-31))).MissedRun.Should().BeTrue();

    [Fact]
    public void MissedRun_WithinGrace_IsNotFlagged()
        => Evaluate(TaskInfo(nextRun: Now.AddMinutes(-29))).MissedRun.Should().BeFalse();

    [Fact]
    public void MissedRun_RunningTask_IsNeverMissed()
        => Evaluate(TaskInfo(state: "Running", lastRun: Now.AddHours(-5), nextRun: Now.AddHours(-4))).MissedRun.Should().BeFalse();

    [Fact]
    public void MissedRun_QueuedTask_IsNeverMissed()
        => Evaluate(TaskInfo(state: "Queued", nextRun: Now.AddHours(-4))).MissedRun.Should().BeFalse();

    [Fact]
    public void MissedRun_DisabledTask_IsNeverMissed()
    {
        Evaluate(TaskInfo(state: "Disabled", enabled: false, nextRun: Now.AddHours(-4))).MissedRun.Should().BeFalse();
        Evaluate(TaskInfo(state: "Ready", enabled: false, nextRun: Now.AddHours(-4))).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRun_NoNextRunOrCheckDisabled_IsNotFlagged()
    {
        Evaluate(TaskInfo(nextRun: null)).MissedRun.Should().BeFalse();
        var watch = Watch(@"\Minicon\*");
        watch.CheckMissedRun = false;
        Evaluate(TaskInfo(nextRun: Now.AddHours(-4)), watch).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRun_GraceOverride()
    {
        var watch = Watch(@"\Minicon\*");
        watch.MissedRunGraceMinutes = 5;
        Evaluate(TaskInfo(nextRun: Now.AddMinutes(-6)), watch).MissedRun.Should().BeTrue();
    }

    // ----- Evaluate (selection + state) ----------------------------------------------------------

    private static RawScheduledTask Raw(string folder, string name, string state = "Ready", bool? enabled = true)
        => new() { TaskPath = folder, TaskName = name, State = state, Enabled = enabled };

    private static ServerScheduledTasksConfig Config(params ScheduledTaskWatchConfig[] watches)
        => new() { Enabled = true, Tasks = watches.ToList() };

    [Fact]
    public void Evaluate_SelectsWatchedTasksSortedByPath()
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(Raw(@"\Minicon\", "Zeta"));
        raw.Tasks.Add(Raw(@"\Minicon\", "Alpha", "Disabled", enabled: true));
        raw.Tasks.Add(Raw(@"\Microsoft\Windows\", "Defrag"));
        raw.Tasks.Add(Raw(@"\", "Other"));

        var state = ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now);

        state.QueryFailed.Should().BeFalse();
        state.LastChecked.Should().Be(Now);
        state.Tasks.Select(t => t.Path).Should().Equal(@"\Minicon\Alpha", @"\Minicon\Zeta");
        state.Tasks[0].Enabled.Should().BeFalse("state Disabled means disabled");
        state.Tasks[0].Name.Should().Be("Alpha");
    }

    [Theory]
    [InlineData("Ready", "Ready")]
    [InlineData("running", "Running")]
    [InlineData("4", "Running")]
    [InlineData("1", "Disabled")]
    [InlineData("2", "Queued")]
    [InlineData(null, "Unknown")]
    [InlineData("whatever", "Unknown")]
    public void NormalizeState_MapsNamesAndNumbers(string? input, string expected)
        => ScheduledTaskChecker.NormalizeState(input).Should().Be(expected);

    // ----- Events ---------------------------------------------------------------------------------

    private static RawScheduledTaskEvent Ev(int id, string taskPath, int minutesAgo = 5, int level = 2, string message = "Fehler")
        => new() { Id = id, TaskPath = taskPath, TimeCreated = Now.AddMinutes(-minutesAgo), Level = level, Message = message };

    private static ScheduledTaskState EvaluateEvents(IEnumerable<RawScheduledTaskEvent> events,
        IReadOnlyList<ScheduledTaskIgnoreRule>? rules = null, ScheduledTaskDefaults? defaults = null, bool checkEvents = true)
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(Raw(@"\Minicon\", "Export"));
        raw.Tasks.Add(Raw(@"\Minicon\", "Import"));
        raw.Events.AddRange(events);
        var config = Config(Watch(@"\Minicon\*"));
        config.CheckEvents = checkEvents;
        return ScheduledTaskChecker.Evaluate(config, defaults ?? new ScheduledTaskDefaults(), raw, rules, Now);
    }

    [Fact]
    public void Events_OnlyErrorIdsOfWatchedTasksAreCounted()
    {
        var state = EvaluateEvents(new[]
        {
            Ev(101, @"\Minicon\Export"),
            Ev(203, @"\minicon\import"),
            Ev(322, @"\Minicon\Export", level: 3),   // already running: not an error
            Ev(330, @"\Minicon\Export", level: 3),   // stopped by user: not an error
            Ev(101, @"\Other\Task"),                 // not watched
            Ev(101, ""),                             // task not resolvable
            Ev(101, @"\Minicon\Export", level: 4),   // informational level
            Ev(101, @"\Minicon\Export", minutesAgo: 120) // outside the window
        });

        state.EventCount.Should().Be(2);
        state.RecentEvents.Select(e => e.EventId).Should().BeEquivalentTo(new[] { 101, 203 });
        state.EventStatus.Should().Be(MetricStatus.Warning);
    }

    [Fact]
    public void Events_LevelIsMappedCultureIndependent()
    {
        var state = EvaluateEvents(new[] { Ev(101, @"\Minicon\Export", level: 1), Ev(111, @"\Minicon\Export", level: 3, minutesAgo: 10) });
        state.RecentEvents.Select(e => e.Level).Should().Equal("Critical", "Warning");
    }

    [Fact]
    public void Events_CriticalThreshold()
    {
        var state = EvaluateEvents(Enumerable.Range(1, 5).Select(i => Ev(101, @"\Minicon\Export", minutesAgo: i * 2)));
        state.EventStatus.Should().Be(MetricStatus.Critical);
    }

    [Fact]
    public void Events_IgnoreRules_AreAppliedAndCountedSeparately()
    {
        var rules = new List<ScheduledTaskIgnoreRule>
        {
            new() { TaskPath = @"\Minicon\Exp*", EventIds = { 101 }, Reason = "bekannt" },
            new() { MessageContains = "egal", ExpiresAt = Now.AddMinutes(-1), Reason = "abgelaufen" }
        };
        var state = EvaluateEvents(new[]
        {
            Ev(101, @"\Minicon\Export"),
            Ev(203, @"\Minicon\Export", minutesAgo: 10, message: "egal"),
            Ev(101, @"\Minicon\Import")
        }, rules);

        state.IgnoredEventCount.Should().Be(1);
        state.EventCount.Should().Be(2);
        state.RecentEvents.Should().NotContain(e => e.EventId == 101 && e.TaskPath == @"\Minicon\Export");
    }

    [Fact]
    public void Events_ServerScopeOfIgnoreRules()
    {
        var rules = new[]
        {
            new ScheduledTaskIgnoreRule { EventIds = { 101 }, Servers = { "SRV07*" }, Reason = "nur srv07" },
            new ScheduledTaskIgnoreRule { EventIds = { 203 }, Reason = "alle Server" }
        };

        ScheduledTaskChecker.RulesForServer(rules, "srv07-app").Should().HaveCount(2);
        var otherServerRules = ScheduledTaskChecker.RulesForServer(rules, "srv08");
        otherServerRules.Should().ContainSingle().Which.Reason.Should().Be("alle Server");
        ScheduledTaskChecker.RulesForServer(null, "srv08").Should().BeEmpty();

        var state = EvaluateEvents(new[] { Ev(101, @"\Minicon\Export"), Ev(203, @"\Minicon\Export", minutesAgo: 10) }, otherServerRules);
        state.EventCount.Should().Be(1);
        state.IgnoredEventCount.Should().Be(1);
    }

    [Fact]
    public void Events_NewestFirst_CappedAndMessagesTruncated()
    {
        var longMessage = new string('x', 2000);
        var defaults = new ScheduledTaskDefaults { MaxReportedEvents = 3, EventCriticalCount = 100 };
        var state = EvaluateEvents(Enumerable.Range(1, 10).Select(i => Ev(101, @"\Minicon\Export", minutesAgo: i * 3, message: longMessage)),
            defaults: defaults);

        state.EventCount.Should().Be(10);
        state.RecentEvents.Should().HaveCount(3);
        state.RecentEvents.Select(e => e.Timestamp).Should().BeInDescendingOrder();
        state.RecentEvents[0].Timestamp.Should().Be(Now.AddMinutes(-3));
        state.RecentEvents[0].Message.Length.Should().Be(ScheduledTaskChecker.MaxMessageLength);
    }

    [Fact]
    public void Events_NotEvaluatedWhenCheckEventsIsOff()
    {
        var state = EvaluateEvents(new[] { Ev(101, @"\Minicon\Export") }, checkEvents: false);
        state.EventCount.Should().Be(0);
        state.RecentEvents.Should().BeEmpty();
        state.EventStatus.Should().Be(MetricStatus.Healthy);
    }

    // ----- JSON parsing ---------------------------------------------------------------------------

    [Fact]
    public void Parse_ArrayOfTasks_WithNullAndNeverDates()
    {
        const string json = """
            {"tasks":[
              {"taskPath":"\\Minicon\\","taskName":"Export","state":"Ready","enabled":true,"description":"Täglicher Export",
               "userId":"SYSTEM","lastRunTime":"2026-11-05T10:00:00.0000000Z","lastTaskResult":2147942402,"nextRunTime":"2026-11-06T10:00:00.0000000Z"},
              {"taskPath":"\\Minicon\\","taskName":"Neu","state":"Ready","enabled":null,"description":"",
               "userId":null,"lastRunTime":"1999-11-30T00:00:00.0000000Z","lastTaskResult":267011,"nextRunTime":null}
            ],"events":[],"queryError":null,"eventError":null}
            """;
        var result = ScheduledTaskChecker.ParseQueryOutput(json);

        result.QueryError.Should().BeNull();
        result.Tasks.Should().HaveCount(2);
        var export = result.Tasks[0];
        export.LastRunTime.Should().Be(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc));
        export.LastRunTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        export.LastTaskResult.Should().Be(unchecked((int)0x80070002));
        export.Description.Should().Be("Täglicher Export");
        export.Enabled.Should().BeTrue();

        var neu = result.Tasks[1];
        neu.LastRunTime.Should().BeNull("1999-11-30 means never ran");
        neu.NextRunTime.Should().BeNull();
        neu.Enabled.Should().BeNull();
        neu.LastTaskResult.Should().Be(0x41303);
    }

    [Fact]
    public void Parse_SingleObjectsInsteadOfArrays()
    {
        const string json = """
            {"tasks":{"taskPath":"\\","taskName":"Solo","state":"Running","enabled":true,"lastRunTime":"2026-11-05T11:00:00+01:00"},
             "events":{"timeCreated":"2026-11-05T11:55:00Z","id":101,"level":2,"taskPath":"\\Solo","message":"Start fehlgeschlagen"}}
            """;
        var result = ScheduledTaskChecker.ParseQueryOutput(json);

        result.Tasks.Should().ContainSingle().Which.TaskName.Should().Be("Solo");
        result.Tasks[0].LastRunTime.Should().Be(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc));
        result.Events.Should().ContainSingle().Which.Id.Should().Be(101);
        result.Events[0].TaskPath.Should().Be(@"\Solo");
    }

    [Fact]
    public void Parse_MinValueDatesAndBareArray()
    {
        var result = ScheduledTaskChecker.ParseQueryOutput("""[{"taskPath":"\\","taskName":"A","lastRunTime":"0001-01-01T00:00:00.0000000"}]""");
        result.Tasks.Should().ContainSingle().Which.LastRunTime.Should().BeNull();
    }

    [Fact]
    public void Parse_QueryErrorAndEventError()
    {
        var result = ScheduledTaskChecker.ParseQueryOutput("\uFEFF{\"tasks\":[],\"events\":[],\"queryError\":\"Zugriff verweigert\",\"eventError\":\"x\"}");
        result.QueryError.Should().Be("Zugriff verweigert");
        result.EventError.Should().Be("x");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Get-ScheduledTask : not recognized")]
    [InlineData("\"text\"")]
    public void Parse_InvalidOutput_Throws(string output)
    {
        var act = () => ScheduledTaskChecker.ParseQueryOutput(output);
        act.Should().Throw<JsonException>();
    }

    // ----- Script hardening -----------------------------------------------------------------------

    private const string Hostile = "\\x'); $global:pwned = 1; $(Remove-Item C:\\) ;('\nnext’line";

    [Fact]
    public void BuildQueryScript_EmbedsPatternsOnlyAsQuotedLiterals()
    {
        var prefixes = ScheduledTaskChecker.BuildPathPrefixes(new[] { Watch(Hostile + "*"), Watch(@"\Minicon\*") });
        var script = ScheduledTaskChecker.BuildQueryScript(prefixes, includeEvents: true, eventWindowMinutes: 60);

        script.Should().Contain("$prefixes = @('\\x''); $global:pwned = 1; $(Remove-Item C:\\) ;(''next’’line', '\\Minicon\\')");
        script.Should().NotContain("x');");
        script.Split('\n').Should().ContainSingle(l => l.Contains("pwned"), "the newline in the pattern must be dropped");
        script.Should().Contain("AddMinutes(-60)").And.Contain("-MaxEvents 500").And.Contain("Id = @(101, 103, 111, 202, 203, 204, 205, 329, 332, 330)");
        script.Should().Contain("ConvertTo-Json -Depth 4 -Compress");
    }

    [Fact]
    public void BuildQueryScript_WithoutEvents()
        => ScheduledTaskChecker.BuildQueryScript(new[] { @"\" }, includeEvents: false, eventWindowMinutes: 60)
            .Should().Contain("if ($false -and");

    // ----- CheckAsync with a fake PowerShell runner ------------------------------------------------

    private static ScheduledTaskChecker Checker(bool isWindows, Func<string, TimeSpan, CancellationToken, Task<PowerShellOutput>> run)
        => new(NullLogger<ScheduledTaskChecker>.Instance, isWindows, run);

    [Fact]
    public async Task CheckAsync_NonWindows_ReturnsEmptyStateWithoutRunningPowerShell()
    {
        var called = false;
        var checker = Checker(false, (_, _, _) => { called = true; return Task.FromResult(new PowerShellOutput(0, "{}", "", false)); });

        var state = await checker.CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults());

        called.Should().BeFalse();
        state.QueryFailed.Should().BeFalse();
        state.Tasks.Should().BeEmpty();
        state.LastChecked.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CheckAsync_Success_EvaluatesOutput()
    {
        var checker = Checker(true, (_, _, _) => Task.FromResult(new PowerShellOutput(0,
            """{"tasks":[{"taskPath":"\\Minicon\\","taskName":"Export","state":"Ready","enabled":true,"lastRunTime":"2026-01-01T00:00:00Z","lastTaskResult":1}],"events":[]}""",
            "", false)));

        var state = await checker.CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults());

        state.QueryFailed.Should().BeFalse();
        state.Tasks.Should().ContainSingle().Which.LastRunFailed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_Timeout_SetsQueryFailed()
    {
        TimeSpan? passedTimeout = null;
        var checker = Checker(true, (_, timeout, _) =>
        {
            passedTimeout = timeout;
            return Task.FromResult(new PowerShellOutput(-1, "", "", true));
        });

        var state = await checker.CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults());

        passedTimeout.Should().Be(TimeSpan.FromMinutes(2));
        state.QueryFailed.Should().BeTrue();
        state.Error.Should().Contain("Zeitüberschreitung");
    }

    [Fact]
    public async Task CheckAsync_StartFailure_InvalidOutputAndQueryError_SetQueryFailed()
    {
        var config = Config(Watch(@"\Minicon\*"));
        var defaults = new ScheduledTaskDefaults();

        var throwing = await Checker(true, (_, _, _) => throw new InvalidOperationException("nicht gefunden")).CheckAsync(config, defaults);
        throwing.QueryFailed.Should().BeTrue();
        throwing.Error.Should().Contain("nicht gefunden");

        var garbage = await Checker(true, (_, _, _) => Task.FromResult(new PowerShellOutput(1, "", "Boom\nmore", false))).CheckAsync(config, defaults);
        garbage.QueryFailed.Should().BeTrue();
        garbage.Error.Should().Contain("Exit-Code 1").And.Contain("Boom").And.NotContain("more");

        var queryError = await Checker(true, (_, _, _) => Task.FromResult(new PowerShellOutput(0,
            """{"tasks":[],"events":[],"queryError":"Zugriff verweigert"}""", "", false))).CheckAsync(config, defaults);
        queryError.QueryFailed.Should().BeTrue();
        queryError.Error.Should().Contain("Zugriff verweigert");
    }

    [Fact]
    public async Task CheckAsync_NoPatterns_DoesNotRunPowerShell()
    {
        var called = false;
        var checker = Checker(true, (_, _, _) => { called = true; return Task.FromResult(new PowerShellOutput(0, "{}", "", false)); });

        var state = await checker.CheckAsync(new ServerScheduledTasksConfig { Enabled = true }, new ScheduledTaskDefaults());

        called.Should().BeFalse();
        state.QueryFailed.Should().BeFalse();
    }

    // ----- Real PowerShell (skipped when pwsh is not available) ----------------------------------

    /// <summary>
    /// Runs the real query script: a hostile pattern must not execute anything, and the script must produce
    /// parseable JSON (Get-ScheduledTask is missing outside Windows, which surfaces as queryError there).
    /// </summary>
    [PowerShellFact]
    public async Task RealPowerShell_HostilePatternDoesNotExecute_AndOutputParses()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"sa-sched-{Guid.NewGuid():N}");
        var hostile = $"\\x'); New-Item -ItemType File -Path \"{marker}\"; $(New-Item -ItemType File -Path \"{marker}\"); ('";
        var script = ScheduledTaskChecker.BuildQueryScript(
            ScheduledTaskChecker.BuildPathPrefixes(new[] { Watch(hostile + "*") }), includeEvents: true, eventWindowMinutes: 60);

        var output = await ScheduledTaskChecker.RunPowerShellAsync(TestPowerShell.Path!, script, TimeSpan.FromMinutes(1), CancellationToken.None);

        File.Exists(marker).Should().BeFalse();
        output.TimedOut.Should().BeFalse();
        var parsed = ScheduledTaskChecker.ParseQueryOutput(output.StdOut);
        if (!OperatingSystem.IsWindows())
            parsed.QueryError.Should().NotBeNull();
    }

    [PowerShellFact]
    public async Task RealPowerShell_TimeoutKillsProcess()
    {
        var sw = Stopwatch.StartNew();
        var output = await ScheduledTaskChecker.RunPowerShellAsync(TestPowerShell.Path!, "Start-Sleep -Seconds 60", TimeSpan.FromSeconds(2), CancellationToken.None);

        output.TimedOut.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    // ===== Round 2 =================================================================================

    // ----- Disabled tasks never fail --------------------------------------------------------------

    [Fact]
    public void EvaluateTask_DisabledTaskWithErrorResult_IsNoFailure()
    {
        Evaluate(TaskInfo(state: "Disabled", enabled: false, lastRun: Now.AddHours(-1), result: 1)).LastRunFailed.Should().BeFalse();
        Evaluate(TaskInfo(state: "Ready", enabled: false, lastRun: Now.AddHours(-1), result: 1)).LastRunFailed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SettingsEnabledFalse_WithErrorResult_IsNoFailure()
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(new RawScheduledTask { TaskPath = @"\Minicon\", TaskName = "Off", State = "Ready", Enabled = false,
                                             LastRunTime = Now.AddHours(-1), LastTaskResult = 1, NextRunTime = Now.AddHours(-3) });
        var task = ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now).Tasks.Single();
        task.Enabled.Should().BeFalse();
        task.LastRunFailed.Should().BeFalse();
        task.MissedRun.Should().BeFalse();
    }

    // ----- Missed-run counter ---------------------------------------------------------------------

    private static ScheduledTaskInfo Counter(int? missed, string state = "Ready", DateTime? lastRun = null, bool enabled = true)
    {
        var t = TaskInfo(state: state, enabled: enabled, lastRun: lastRun ?? Now.AddDays(-1), nextRun: Now.AddHours(1));
        t.NumberOfMissedRuns = missed;
        return t;
    }

    private static ScheduledTaskInfo EvaluateWithPrevious(ScheduledTaskInfo info, ScheduledTaskInfo? previous)
    {
        ScheduledTaskChecker.EvaluateTask(info, Watch(@"\Minicon\*"), new ScheduledTaskDefaults(), Now, previous);
        return info;
    }

    [Fact]
    public void MissedRunCounter_FirstCycleWithoutPreviousValue_NoAlarm()
    {
        EvaluateWithPrevious(Counter(5), previous: null).MissedRun.Should().BeFalse();
        EvaluateWithPrevious(Counter(5), Counter(null)).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRunCounter_Rise_IsMissedRun()
    {
        var info = EvaluateWithPrevious(Counter(3), Counter(2));
        info.MissedRunsIncreased.Should().BeTrue();
        info.MissedRun.Should().BeTrue();
        EvaluateWithPrevious(Counter(2), Counter(2)).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRunCounter_StaysFlaggedUntilTaskRunsAgain()
    {
        var first = EvaluateWithPrevious(Counter(3), Counter(2));
        var unchanged = EvaluateWithPrevious(Counter(3), first);
        unchanged.MissedRun.Should().BeTrue("no run since the counter rose");

        var ranAgain = EvaluateWithPrevious(Counter(3, lastRun: Now.AddMinutes(-2)), unchanged);
        ranAgain.MissedRunsIncreased.Should().BeFalse();
        ranAgain.MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRunCounter_NegativeConditionsStillApply()
    {
        EvaluateWithPrevious(Counter(3, state: "Running"), Counter(2)).MissedRun.Should().BeFalse();
        EvaluateWithPrevious(Counter(3, state: "Queued"), Counter(2)).MissedRun.Should().BeFalse();
        EvaluateWithPrevious(Counter(3, state: "Disabled", enabled: false), Counter(2)).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UsesPreviousStateByPath()
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(new RawScheduledTask { TaskPath = @"\Minicon\", TaskName = "Export", State = "Ready", Enabled = true,
                                             LastRunTime = Now.AddDays(-1), NextRunTime = Now.AddHours(1), NumberOfMissedRuns = 4 });
        var previous = new ScheduledTaskState { Tasks = { new ScheduledTaskInfo { Path = @"\MINICON\export", NumberOfMissedRuns = 1, LastRunTime = Now.AddDays(-1) } } };

        var task = ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now, previous).Tasks.Single();
        task.NumberOfMissedRuns.Should().Be(4);
        task.MissedRun.Should().BeTrue();
    }

    // ----- Pattern hits, info errors, event log availability --------------------------------------

    [Fact]
    public void Evaluate_PatternMatchesCountsSelectedTasksPerPattern()
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(Raw(@"\Minicon\", "A"));
        raw.Tasks.Add(Raw(@"\Minicon\", "B"));
        raw.Tasks.Add(Raw(@"\Minicon\", "Skip"));

        var state = ScheduledTaskChecker.Evaluate(
            Config(Watch(@"\Minicon\*", @"\Minicon\Skip"), Watch(@"\Typo\*"), Watch("")), new ScheduledTaskDefaults(), raw, null, Now);

        state.PatternMatches.Should().BeEquivalentTo(new Dictionary<string, int> { [@"\Minicon\*"] = 2, [@"\Typo\*"] = 0 });
    }

    [Fact]
    public void Evaluate_InfoErrorIsReported()
    {
        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(new RawScheduledTask { TaskPath = @"\Minicon\", TaskName = "X", State = "Ready", InfoError = "Zugriff verweigert" });
        ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now)
            .Tasks.Single().InfoError.Should().Be("Zugriff verweigert");
    }

    [Fact]
    public void Evaluate_EventLogDisabledAndEventError_OnlyWhenEventsAreChecked()
    {
        var raw = new ScheduledTaskQueryResult { EventLogDisabled = true, EventError = "kaputt" };
        var on = ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now);
        on.EventLogDisabled.Should().BeTrue();
        on.EventError.Should().Be("kaputt");

        var config = Config(Watch(@"\Minicon\*"));
        config.CheckEvents = false;
        var off = ScheduledTaskChecker.Evaluate(config, new ScheduledTaskDefaults(), raw, null, Now);
        off.EventLogDisabled.Should().BeFalse();
        off.EventError.Should().BeNull();
    }

    [Fact]
    public void Factories_ForNewSignals()
    {
        ActiveProblem.ScheduledTaskEventsUnavailable(ScheduledTaskState.EventLogDisabledReason).Severity.Should().Be(ProblemSeverity.Warning);
        ActiveProblem.ScheduledTaskEventsUnavailable("x", isWarning: false).Severity.Should().Be(ProblemSeverity.Info);
        ActiveProblem.ScheduledTaskPatternNoMatch(@"\Typo\*").Message.Should().Contain(@"\Typo\*");
        ActiveProblem.ScheduledTaskInfoUnavailable(@"\Minicon\X", "Zugriff verweigert").Id.Should().Be("schtask__minicon_x_infoerror");
    }

    // ----- One run counts once --------------------------------------------------------------------

    private static RawScheduledTaskEvent Run(int id, string activity, int level = 2, int secondsAgo = 300, string task = @"\Minicon\Export")
        => new() { Id = id, Level = level, ActivityId = activity, TaskPath = task, TimeCreated = Now.AddSeconds(-secondsAgo), Message = $"Event {id}" };

    [Fact]
    public void Events_TimeoutAndTerminated_OfOneRun_CountOnce()
    {
        var state = EvaluateEvents(new[] { Run(329, "{AAAA-1}", level: 3), Run(111, "aaaa-1", level: 3, secondsAgo: 299) });
        state.EventCount.Should().Be(1);
    }

    [Fact]
    public void Events_StartFailureChain_OfOneRun_CountsOnce_MostSevereWins()
    {
        var state = EvaluateEvents(new[] { Run(203, "run-1"), Run(103, "run-1", level: 1), Run(101, "run-1") });
        state.EventCount.Should().Be(1);
        state.RecentEvents.Single().EventId.Should().Be(103);
        state.RecentEvents.Single().ActivityId.Should().Be("run-1");
    }

    [Fact]
    public void Events_TerminatedAfterUserStop_IsNotCounted()
    {
        EvaluateEvents(new[] { Run(111, "run-2", level: 3), Run(330, "run-2", level: 4) }).EventCount.Should().Be(0);
        EvaluateEvents(new[] { Run(111, "", level: 3), Run(330, "", level: 4, secondsAgo: 290) }).EventCount.Should().Be(0);
        EvaluateEvents(new[] { Run(111, "run-3", level: 3), Run(330, "run-4", level: 4) }).EventCount.Should().Be(1,
            "a user stop of another run does not suppress");
    }

    [Fact]
    public void Events_DifferentRunsCountSeparately_AndWithoutActivityIdDuplicatesCollapse()
    {
        EvaluateEvents(new[] { Run(101, "run-a"), Run(101, "run-b") }).EventCount.Should().Be(2);
        EvaluateEvents(new[] { Run(101, ""), Run(101, "") }).EventCount.Should().Be(1);
        EvaluateEvents(new[] { Run(101, ""), Run(101, "", secondsAgo: 200) }).EventCount.Should().Be(2);
    }

    // ----- Cap per task vs. central ignore rules --------------------------------------------------

    [Fact]
    public void Events_NoisyTaskDoesNotCrowdOutOthers_AndCentralIgnoreKeepsOtherTaskVisible()
    {
        var defaults = new ScheduledTaskDefaults { MaxReportedEvents = 50, EventWarningCount = 1, EventCriticalCount = 100 };
        var noisy = Enumerable.Range(1, 60).Select(i => Run(101, $"a-{i}", secondsAgo: i, task: @"\Minicon\Export"));
        var quiet = new[] { Run(101, "b-1", secondsAgo: 1000, task: @"\Minicon\Import") };

        var state = EvaluateEvents(noisy.Concat(quiet), defaults: defaults);
        state.EventCount.Should().Be(61);
        state.RecentEvents.Count(e => e.TaskPath == @"\Minicon\Export").Should().Be(ScheduledTaskChecker.MaxReportedEventsPerTask);
        state.RecentEvents.Should().Contain(e => e.TaskPath == @"\Minicon\Import");

        // Central rule ignores the noisy task completely (via EventGroups): B stays visible and counted.
        var rules = new[] { new ScheduledTaskIgnoreRule { TaskPath = @"Minicon\Export", Reason = "laut" } };
        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1", rules, defaults, Now).Should().Be(60);
        state.RecentEvents.Should().ContainSingle().Which.TaskPath.Should().Be(@"\Minicon\Import");
        state.EventCount.Should().Be(1);
        state.EventStatus.Should().Be(MetricStatus.Warning);
    }

    [Fact]
    public void ApplyCentral_ListBecomesEmpty_EventCountIsNotReset()
    {
        var state = new ScheduledTaskState
        {
            EventCount = 30,
            EventStatus = MetricStatus.Critical,
            RecentEvents = { new ScheduledTaskEvent { TaskPath = @"\Minicon\Export", EventId = 101 } }
        };
        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { EventIds = { 101 }, Reason = "x" } }, new ScheduledTaskDefaults(), Now);
        state.RecentEvents.Should().BeEmpty();
        state.EventCount.Should().Be(29);
        state.EventStatus.Should().Be(MetricStatus.Critical);
    }

    [Fact]
    public void IgnoreMatcher_NormalizesTaskPath()
        => ScheduledTaskIgnoreMatcher.IsIgnored(
                new ScheduledTaskEvent { TaskPath = @"\Minicon\Export", EventId = 101 },
                new[] { new ScheduledTaskIgnoreRule { TaskPath = @"Minicon\Export", Reason = "x" } }, Now)
            .Should().BeTrue();

    [Theory]
    [InlineData(@"Minicon\Export", @"\Minicon\Export")]
    [InlineData(@"  \Minicon\Export ", @"\Minicon\Export")]
    [InlineData("", "")]
    public void ScheduledTaskPaths_NormalizePath(string input, string expected)
        => ScheduledTaskPaths.NormalizePath(input).Should().Be(expected);

    [Theory]
    [InlineData(@"Minicon\*", @"\Minicon\*")]
    [InlineData("*Export*", "*Export*")]
    [InlineData("?x", "?x")]
    public void ScheduledTaskPaths_NormalizePattern(string input, string expected)
        => ScheduledTaskPaths.NormalizePattern(input).Should().Be(expected);

    // ----- CheckAsync edge cases ------------------------------------------------------------------

    [Fact]
    public async Task CheckAsync_PowerShellNotFound_GermanMessage()
    {
        var state = await Checker(true, (_, _, _) => throw new Win32Exception(2))
            .CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults());
        state.QueryFailed.Should().BeTrue();
        state.Error.Should().Be("powershell.exe nicht gefunden");
    }

    [Fact]
    public async Task CheckAsync_NonZeroExitCodeWithValidJson_UsesTheData()
    {
        var state = await Checker(true, (_, _, _) => Task.FromResult(new PowerShellOutput(1,
                """{"tasks":[{"taskPath":"\\Minicon\\","taskName":"Export","state":"Ready","enabled":true}],"events":[]}""", "späte Warnung", false)))
            .CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults());
        state.QueryFailed.Should().BeFalse();
        state.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var checker = Checker(true, (_, _, ct) => Task.FromCanceled<PowerShellOutput>(ct));
        var act = () => checker.CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckAsync_PassesPreviousStateToEvaluation()
    {
        var previous = new ScheduledTaskState { Tasks = { new ScheduledTaskInfo { Path = @"\Minicon\Export", NumberOfMissedRuns = 0, LastRunTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } } };
        var checker = Checker(true, (_, _, _) => Task.FromResult(new PowerShellOutput(0,
            """{"tasks":{"taskPath":"\\Minicon\\","taskName":"Export","state":"Ready","enabled":true,"lastRunTime":"2026-01-01T00:00:00Z","nextRunTime":"2099-01-01T00:00:00Z","numberOfMissedRuns":1}}""", "", false)));
        var state = await checker.CheckAsync(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), previous: previous);
        state.Tasks.Single().MissedRun.Should().BeTrue();
    }

    [Fact]
    public void Parse_NewFields()
    {
        var result = ScheduledTaskChecker.ParseQueryOutput("""
            {"tasks":{"taskPath":"\\X\\","taskName":"Y","numberOfMissedRuns":7,"infoError":"Zugriff verweigert"},
             "events":{"id":101,"level":2,"taskPath":"\\X\\Y","activityId":"6f1e2d3c-0000-0000-0000-000000000001"},
             "eventLogDisabled":true}
            """);
        result.Tasks.Single().NumberOfMissedRuns.Should().Be(7);
        result.Tasks.Single().InfoError.Should().Be("Zugriff verweigert");
        result.Events.Single().ActivityId.Should().Be("6f1e2d3c-0000-0000-0000-000000000001");
        result.EventLogDisabled.Should().BeTrue();
    }

    // ----- Real PowerShell with stubbed cmdlets ---------------------------------------------------

    private const string TaskStubs = """
        function Get-ScheduledTask {
            @(
                [pscustomobject]@{ TaskPath = '\Minicon\'; TaskName = 'Export Äöü'; State = 'Ready'; Description = 'Täglicher Export';
                                   Settings = [pscustomobject]@{ Enabled = $true }; Principal = [pscustomobject]@{ UserId = 'SYSTEM' } },
                [pscustomobject]@{ TaskPath = '\Minicon\'; TaskName = 'Broken'; State = 'Ready'; Description = $null;
                                   Settings = [pscustomobject]@{ Enabled = $true }; Principal = $null },
                [pscustomobject]@{ TaskPath = '\Microsoft\Windows\Minicon\'; TaskName = 'Export Fake'; State = 'Ready';
                                   Settings = [pscustomobject]@{ Enabled = $true }; Principal = $null },
                [pscustomobject]@{ TaskPath = '\Other\'; TaskName = 'X'; State = 'Ready';
                                   Settings = [pscustomobject]@{ Enabled = $true }; Principal = $null }
            )
        }
        function Get-ScheduledTaskInfo {
            param([Parameter(ValueFromPipeline = $true)] $InputObject)
            process {
                if ($InputObject.TaskName -eq 'Broken') { throw 'Zugriff verweigert' }
                [pscustomobject]@{ LastRunTime = [datetime]'1999-11-30'; LastTaskResult = [uint32]2147942402;
                                   NextRunTime = $null; NumberOfMissedRuns = [uint32]3 }
            }
        }

        """;

    private static string EventStubs(bool logEnabled) => $$"""
        function Get-WinEvent {
            param([string] $ListLog, [hashtable] $FilterHashtable, [int] $MaxEvents)
            if ($ListLog) { return [pscustomobject]@{ IsEnabled = ${{(logEnabled ? "true" : "false")}} } }
            $other = [pscustomobject]@{ Id = 101; Level = 2; TimeCreated = (Get-Date); ActivityId = [guid]::NewGuid();
                                        Properties = @([pscustomobject]@{ Value = '\Other\X' }) }
            $other | Add-Member -MemberType ScriptProperty -Name Message -Value { throw 'message of a foreign task was read' }
            $mine = [pscustomobject]@{ Id = 101; Level = 2; TimeCreated = (Get-Date); ActivityId = [guid]'6f1e2d3c-0000-0000-0000-000000000001';
                                       Properties = @([pscustomobject]@{ Value = '\Minicon\Export Äöü' }); Message = 'Start fehlgeschlagen – Zugriff' }
            @($other, $mine)
        }

        """;

    private static async Task<ScheduledTaskQueryResult> RunStubbed(string pattern, bool logEnabled)
    {
        var script = TaskStubs + EventStubs(logEnabled) + ScheduledTaskChecker.BuildQueryScript(
            ScheduledTaskChecker.BuildPathPrefixes(new[] { Watch(pattern) }), includeEvents: true, eventWindowMinutes: 60);
        var output = await ScheduledTaskChecker.RunPowerShellAsync(TestPowerShell.Path!, script, TimeSpan.FromMinutes(1), CancellationToken.None);
        output.TimedOut.Should().BeFalse();
        output.ExitCode.Should().Be(0, output.StdErr);
        return ScheduledTaskChecker.ParseQueryOutput(output.StdOut);
    }

    [PowerShellFact]
    public async Task RealPowerShell_StubbedCmdlets_ProduceParseableData()
    {
        var result = await RunStubbed(@"\Minicon\Export*", logEnabled: true);

        result.QueryError.Should().BeNull();
        result.EventError.Should().BeNull("the message of a foreign task must not be read");
        result.EventLogDisabled.Should().BeFalse();

        var task = result.Tasks.Should().ContainSingle("\\Microsoft\\ and foreign tasks are filtered").Subject;
        ScheduledTaskChecker.BuildTaskPath(task.TaskPath, task.TaskName).Should().Be(@"\Minicon\Export Äöü");
        task.Description.Should().Be("Täglicher Export");
        task.UserId.Should().Be("SYSTEM");
        task.LastTaskResult.Should().Be(unchecked((int)0x80070002));
        task.LastRunTime.Should().BeNull("1999-11-30 means never ran");
        task.NextRunTime.Should().BeNull();
        task.NumberOfMissedRuns.Should().Be(3);
        task.Enabled.Should().BeTrue();

        var ev = result.Events.Should().ContainSingle().Subject;
        ev.TaskPath.Should().Be(@"\Minicon\Export Äöü");
        ev.ActivityId.Should().Be("6f1e2d3c-0000-0000-0000-000000000001");
        ev.Message.Should().Be("Start fehlgeschlagen – Zugriff");
        ev.TimeCreated.Should().NotBeNull();
        ev.TimeCreated!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [PowerShellFact]
    public async Task RealPowerShell_StubbedCmdlets_InfoErrorAndDisabledLog()
    {
        var result = await RunStubbed(@"\Minicon\*", logEnabled: false);

        result.EventLogDisabled.Should().BeTrue();
        result.Events.Should().BeEmpty();
        result.Tasks.Should().HaveCount(2);
        result.Tasks.Single(t => t.TaskName == "Broken").InfoError.Should().Contain("Zugriff verweigert");
    }

    [PowerShellFact]
    public async Task RealPowerShell_CallerCancellationKillsAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        var act = () => ScheduledTaskChecker.RunPowerShellAsync(TestPowerShell.Path!, "Start-Sleep -Seconds 60", TimeSpan.FromMinutes(1), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [PowerShellFact]
    public async Task RealPowerShell_NonZeroExitCodeIsReported()
    {
        var output = await ScheduledTaskChecker.RunPowerShellAsync(TestPowerShell.Path!, "'{}'; exit 3", TimeSpan.FromMinutes(1), CancellationToken.None);
        output.ExitCode.Should().Be(3);
        output.StdOut.Trim().Should().Be("{}");
    }

    // ===== Round 3 =================================================================================

    // ----- EventGroups + central ignore rules -----------------------------------------------------

    [Fact]
    public void EventGroups_AggregatePerTaskAndEventId_Uncapped()
    {
        var events = Enumerable.Range(1, 30).Select(i => Run(101, $"r-{i}", secondsAgo: i * 10))
            .Append(Run(203, "x-1", task: @"\Minicon\Import"));
        var state = EvaluateEvents(events);

        state.EventGroups.Should().NotBeNull();
        state.EventGroups!.Should().HaveCount(2);
        var export = state.EventGroups[0];
        export.TaskPath.Should().Be(@"\Minicon\Export");
        export.EventId.Should().Be(101);
        export.Count.Should().Be(30);
        export.LastSeen.Should().Be(Now.AddSeconds(-10));
        export.SampleMessage.Should().Be("Event 101");
        state.RecentEvents.Count(e => e.TaskPath == @"\Minicon\Export").Should().Be(ScheduledTaskChecker.MaxReportedEventsPerTask);
    }

    [Fact]
    public void ApplyCentral_ThirtyRunsOfOneTask_RuleSilencesItCompletely()
    {
        var defaults = new ScheduledTaskDefaults();
        var state = EvaluateEvents(Enumerable.Range(1, 30).Select(i => Run(101, $"r-{i}", secondsAgo: i * 10)), defaults: defaults);
        state.EventCount.Should().Be(30);
        state.EventStatus.Should().Be(MetricStatus.Critical);

        var removed = ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { TaskPath = @"\Minicon\Export", EventIds = { 101 }, Reason = "bekannt" } }, defaults, Now);

        removed.Should().Be(30);
        state.EventCount.Should().Be(0);
        state.EventStatus.Should().Be(MetricStatus.Healthy);
        state.RecentEvents.Should().BeEmpty();
        state.EventGroups.Should().BeEmpty();
        state.IgnoredEventCount.Should().Be(30);
    }

    [Fact]
    public void ApplyCentral_OtherTaskStaysCounted()
    {
        var defaults = new ScheduledTaskDefaults();
        var events = Enumerable.Range(1, 30).Select(i => Run(101, $"r-{i}", secondsAgo: i * 10))
            .Concat(Enumerable.Range(1, 2).Select(i => Run(203, $"i-{i}", secondsAgo: i * 10, task: @"\Minicon\Import")));
        var state = EvaluateEvents(events, defaults: defaults);

        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { TaskPath = @"\Minicon\Export", Reason = "bekannt" } }, defaults, Now);

        state.EventCount.Should().Be(2);
        state.EventStatus.Should().Be(MetricStatus.Warning);
        state.RecentEvents.Should().OnlyContain(e => e.TaskPath == @"\Minicon\Import");
    }

    [Fact]
    public void ApplyCentral_MessageContainsMatchesSampleMessage_AndKeepsEventsBeyondGroupCap()
    {
        var state = new ScheduledTaskState
        {
            EventCount = 12, // 2 more than the groups hold (beyond the group cap)
            EventStatus = MetricStatus.Critical,
            EventGroups = new()
            {
                new() { TaskPath = @"\Minicon\Export", EventId = 203, Count = 7, SampleMessage = "Datei gesperrt" },
                new() { TaskPath = @"\Minicon\Import", EventId = 203, Count = 3, SampleMessage = "Netzwerkfehler" }
            }
        };

        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { MessageContains = "gesperrt", Reason = "x" } }, new ScheduledTaskDefaults(), Now).Should().Be(7);

        state.EventCount.Should().Be(5);
        state.EventGroups.Should().ContainSingle().Which.TaskPath.Should().Be(@"\Minicon\Import");
        state.EventStatus.Should().Be(MetricStatus.Critical, "5 >= critical threshold 5, never escalated");
    }

    [Fact]
    public void ApplyCentral_WithoutEventGroups_UsesLegacyLogic()
    {
        var state = new ScheduledTaskState
        {
            EventCount = 30,
            EventStatus = MetricStatus.Critical,
            RecentEvents = { new ScheduledTaskEvent { TaskPath = @"\Minicon\Export", EventId = 101 } }
        };
        state.EventGroups.Should().BeNull();
        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { TaskPath = @"\Minicon\Export", Reason = "x" } }, new ScheduledTaskDefaults(), Now).Should().Be(1);
        state.EventCount.Should().Be(29);
    }

    [Fact]
    public void ApplyCentral_NeverEscalates()
    {
        var state = new ScheduledTaskState
        {
            EventCount = 10,
            EventStatus = MetricStatus.Warning,
            EventGroups = new() { new() { TaskPath = @"\A", EventId = 101, Count = 9 }, new() { TaskPath = @"\B", EventId = 101, Count = 1 } }
        };
        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { TaskPath = @"\B", Reason = "x" } }, new ScheduledTaskDefaults(), Now);
        state.EventCount.Should().Be(9);
        state.EventStatus.Should().Be(MetricStatus.Warning);
    }

    // ----- Fallback without ActivityId: path + time window ----------------------------------------

    [Fact]
    public void Events_WithoutActivityId_StartFailureChainCountsOnce()
    {
        var state = EvaluateEvents(new[] { Run(203, "", secondsAgo: 300), Run(101, "", secondsAgo: 299), Run(103, "", level: 1, secondsAgo: 298) });
        state.EventCount.Should().Be(1);
        state.RecentEvents.Single().EventId.Should().Be(103);
    }

    [Fact]
    public void Events_WithoutActivityId_DifferentTasksOrFarApart_CountSeparately()
    {
        EvaluateEvents(new[] { Run(101, ""), Run(101, "", task: @"\Minicon\Import") }).EventCount.Should().Be(2);
        EvaluateEvents(new[] { Run(101, "", secondsAgo: 300), Run(203, "", secondsAgo: 230) }).EventCount.Should().Be(2);
    }

    // ----- Missed-run carry-forward ---------------------------------------------------------------

    [Fact]
    public void MissedRunCounter_QueryFailed_CarriesLastKnownTasks()
    {
        var flagged = EvaluateWithPrevious(Counter(3), Counter(2));
        flagged.MissedRunsIncreasedAt.Should().Be(Now);
        var good = new ScheduledTaskState { Tasks = { flagged } };

        var failed = ScheduledTaskChecker.Failed(good, Now.AddMinutes(5), "Zeitüberschreitung");
        failed.QueryFailed.Should().BeTrue();
        failed.Tasks.Should().BeEmpty();
        failed.CarriedTasks.Should().ContainSingle().Which.NumberOfMissedRuns.Should().Be(3);

        // A second failed cycle keeps carrying; recovery evaluates against the carried task.
        var failedAgain = ScheduledTaskChecker.Failed(failed, Now.AddMinutes(10), "Zeitüberschreitung");
        failedAgain.CarriedTasks.Should().BeSameAs(failed.CarriedTasks);

        var raw = new ScheduledTaskQueryResult();
        raw.Tasks.Add(new RawScheduledTask { TaskPath = @"\Minicon\", TaskName = "Export", State = "Ready", Enabled = true,
                                             LastRunTime = flagged.LastRunTime, NextRunTime = Now.AddHours(1), NumberOfMissedRuns = 3 });
        var recovered = ScheduledTaskChecker.Evaluate(Config(Watch(@"\Minicon\*")), new ScheduledTaskDefaults(), raw, null, Now.AddMinutes(15), failedAgain);
        recovered.Tasks.Single().MissedRun.Should().BeTrue("the flag survived the failed query");
        recovered.CarriedTasks.Should().BeNull();
    }

    [Fact]
    public void MissedRunCounter_MissingCounter_KeepsLastKnownValueAndFlag()
    {
        var flagged = EvaluateWithPrevious(Counter(3), Counter(2));
        var noInfo = EvaluateWithPrevious(Counter(null), flagged);
        noInfo.NumberOfMissedRuns.Should().Be(3);
        noInfo.MissedRunsIncreased.Should().BeTrue();
        noInfo.MissedRun.Should().BeTrue();

        // Back with a counter: still the same value, no new run -> stays flagged.
        EvaluateWithPrevious(Counter(3), noInfo).MissedRun.Should().BeTrue();
    }

    [Fact]
    public void MissedRunCounter_DisabledTask_IsNotCarried()
    {
        var flagged = EvaluateWithPrevious(Counter(3), Counter(2));
        var disabled = EvaluateWithPrevious(Counter(3, state: "Disabled", enabled: false), flagged);
        disabled.MissedRunsIncreased.Should().BeFalse();
        disabled.MissedRunsIncreasedAt.Should().BeNull();

        var disabledNoCounter = EvaluateWithPrevious(Counter(null, enabled: false), flagged);
        disabledNoCounter.MissedRunsIncreased.Should().BeFalse();
        disabledNoCounter.NumberOfMissedRuns.Should().BeNull();

        // Re-enabled with the same counter: no carried flag from the disabled period.
        EvaluateWithPrevious(Counter(3), disabled).MissedRun.Should().BeFalse();
    }

    [Fact]
    public void MissedRunCounter_ExpiresWithoutNextRunOrAfter24Hours()
    {
        var flagged = EvaluateWithPrevious(Counter(3), Counter(2));

        var noNextRun = Counter(3);
        noNextRun.NextRunTime = null;
        EvaluateWithPrevious(noNextRun, flagged).MissedRunsIncreased.Should().BeFalse();

        var old = Counter(3);
        old.NumberOfMissedRuns = 3;
        flagged.MissedRunsIncreasedAt = Now.AddHours(-25);
        EvaluateWithPrevious(old, flagged).MissedRunsIncreased.Should().BeFalse();

        flagged.MissedRunsIncreasedAt = Now.AddHours(-23);
        EvaluateWithPrevious(Counter(3), flagged).MissedRunsIncreased.Should().BeTrue();
    }

    // ===== Final touches ===========================================================================

    [Fact]
    public void HttpCopy_DropsCarriedTasks_OriginalUnchanged()
    {
        var carried = new List<ScheduledTaskInfo> { new() { Path = @"\Minicon\Export", NumberOfMissedRuns = 3 } };
        var original = new Minicon.SimpleAdmin.Models.Status.RuntimeStatus
        {
            Server = "srv1",
            Service = "web",
            ScheduledTasks = new ScheduledTaskState { QueryFailed = true, Error = "Zeitüberschreitung", CarriedTasks = carried, EventCount = 4 }
        };

        var http = Minicon.SimpleAdmin.HtmlGenerator.StaticHtmlGenerator.WithoutHistory(original);

        http.ScheduledTasks.Should().NotBeNull();
        http.ScheduledTasks!.CarriedTasks.Should().BeNull();
        http.ScheduledTasks.QueryFailed.Should().BeTrue();
        http.ScheduledTasks.Error.Should().Be("Zeitüberschreitung");
        http.ScheduledTasks.EventCount.Should().Be(4);
        original.ScheduledTasks!.CarriedTasks.Should().BeSameAs(carried);

        System.Text.Json.JsonSerializer.Serialize(http.ScheduledTasks).Should().NotContain("CarriedTasks\":[");
    }

    [Fact]
    public void ApplyCentral_EntryOfKeptGroupStaysInList()
    {
        var state = new ScheduledTaskState
        {
            EventCount = 2,
            EventStatus = MetricStatus.Warning,
            EventGroups = new() { new() { TaskPath = @"\Minicon\Export", EventId = 203, Count = 2, SampleMessage = "Netzwerkfehler" } },
            RecentEvents =
            {
                new ScheduledTaskEvent { TaskPath = @"\Minicon\Export", EventId = 203, Message = "Netzwerkfehler" },
                new ScheduledTaskEvent { TaskPath = @"\Minicon\Export", EventId = 203, Message = "Datei gesperrt" }
            }
        };

        ScheduledTaskIgnoreMatcher.ApplyCentral(state, "srv1",
            new[] { new ScheduledTaskIgnoreRule { MessageContains = "gesperrt", Reason = "x" } }, new ScheduledTaskDefaults(), Now).Should().Be(0);

        state.RecentEvents.Should().HaveCount(2, "the group stays, so its entries stay");
        state.EventCount.Should().Be(2);
        state.IgnoredEventCount.Should().Be(0);
    }
}
