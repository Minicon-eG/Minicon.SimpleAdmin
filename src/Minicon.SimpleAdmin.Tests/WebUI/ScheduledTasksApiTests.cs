using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers.Api;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;
using Minicon.SimpleAdmin.WebUI.ViewModels;
using static Minicon.SimpleAdmin.Services.ScheduledTaskGroupEvaluator;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class ScheduledTasksApiTests : IDisposable
{
    private const string TaskPath = @"\Minicon\Export";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-sched-api-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IScheduledTaskControl> _control = new(MockBehavior.Strict);

    public ScheduledTasksApiTests()
    {
        Directory.CreateDirectory(_dir);
        _control.SetupGet(c => c.IsEnabled).Returns(true);
        _control.SetupGet(c => c.DisabledReason).Returns((string?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static Server TaskServer(string name, string? id = null) => new()
    {
        Name = name,
        Id = id,
        Active = true,
        Checks = new ServerChecksConfig { ScheduledTasks = new ServerScheduledTasksConfig { Enabled = true } }
    };

    private ConfigurationService LoadConfig(Config config)
    {
        var service = new ConfigurationService(new ConfigurationBuilder().Build());
        var path = Path.Combine(_dir, "config.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json);
        service.LoadFromFile(json, path);
        return service;
    }

    private ConfigurationService DefaultConfig() => LoadConfig(new Config
    {
        Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = true } },
        Servers = new List<Server>
        {
            TaskServer("srv01"),
            TaskServer("srv02"),
            TaskServer("srv03"),
            new() { Name = "srv-noTasks", Active = true }
        }
    });

    private static GroupMember Member(string server, bool enabled) =>
        new(server, enabled, enabled ? "Ready" : "Disabled", new ScheduledTaskInfo { Path = TaskPath, Name = "Export", Enabled = enabled });

    /// <summary>srv01 + srv02 reported the task (both enabled), srv03 is unknown.</summary>
    private static IReadOnlyList<TaskGroup> DefaultGroups() => new[]
    {
        new TaskGroup("production", TaskPath, ScheduledTaskMode.SingleActive,
            new[] { Member("srv01", true), Member("srv02", true) },
            new[] { "srv03" },
            "srv01")
    };

    private ScheduledTasksController Controller(ConfigurationService config, IReadOnlyList<TaskGroup>? groups = null, string? user = @"DOM\alice")
    {
        var controller = new ScheduledTasksController(_control.Object, config, () => Task.FromResult(groups ?? DefaultGroups()));
        var identity = user == null ? new ClaimsIdentity() : new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "Negotiate");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static ScheduledTaskActionResult Ok(string server, ScheduledTaskAction action) =>
        new(server, TaskPath, action, true, "OK");

    // ── Gate ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Action_WhenControlDisabled_Returns403WithReasonAndDoesNotCall()
    {
        _control.SetupGet(c => c.IsEnabled).Returns(false);
        _control.SetupGet(c => c.DisabledReason).Returns("Anmeldung ist abgeschaltet");
        var controller = Controller(DefaultConfig());

        var result = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Server = "srv01", Path = TaskPath, Action = ScheduledTaskAction.Disable },
            CancellationToken.None);

        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.Serialize(forbidden.Value).Should().Contain("Anmeldung ist abgeschaltet");
        _control.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScheduledTaskAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExclusiveAndLive_WhenControlDisabled_Return403()
    {
        _control.SetupGet(c => c.IsEnabled).Returns(false);
        var controller = Controller(DefaultConfig());

        var exclusive = await controller.ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = TaskPath, TargetServer = "srv01" }, CancellationToken.None);
        var live = await controller.Live("srv01", TaskPath, CancellationToken.None);

        exclusive.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        live.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _control.VerifyGet(c => c.IsEnabled);
        _control.VerifyGet(c => c.DisabledReason);
        _control.VerifyNoOtherCalls();
    }

    // ── Target validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("srv99", TaskPath)]             // not in config.json
    [InlineData("srv-noTasks", TaskPath)]       // in config, but without scheduled task monitoring
    [InlineData("srv01", @"\Minicon\Other")]    // path not reported
    [InlineData("evil.example.com", TaskPath)]  // free host → never contacted
    public async Task Action_UnknownServerOrPath_Returns404WithoutCall(string server, string path)
    {
        var controller = Controller(DefaultConfig());

        var result = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Server = server, Path = path, Action = ScheduledTaskAction.Run },
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        _control.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScheduledTaskAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Action_MissingInput_Returns400WithoutCall()
    {
        var controller = Controller(DefaultConfig());

        var noServer = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Path = TaskPath, Action = ScheduledTaskAction.Run }, CancellationToken.None);
        var noAction = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Server = "srv01", Path = TaskPath }, CancellationToken.None);
        var badAction = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Server = "srv01", Path = TaskPath, Action = (ScheduledTaskAction)42 }, CancellationToken.None);

        noServer.Should().BeOfType<BadRequestObjectResult>();
        noAction.Should().BeOfType<BadRequestObjectResult>();
        badAction.Should().BeOfType<BadRequestObjectResult>();
        _control.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScheduledTaskAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Action_KnownTarget_PassesConfigNameCanonicalPathAndUser()
    {
        // Status reports the server by its ID; the control gets the config name and the reported path casing.
        var config = LoadConfig(new Config
        {
            Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = true } },
            Servers = new List<Server> { TaskServer("srv01.dom.local", id: "srv01") }
        });
        var groups = new[]
        {
            new TaskGroup("production", TaskPath, ScheduledTaskMode.AllServers, new[] { Member("srv01", false) }, Array.Empty<string>(), "srv01")
        };
        _control.Setup(c => c.ExecuteAsync("srv01.dom.local", TaskPath, ScheduledTaskAction.Enable, @"DOM\alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok("srv01.dom.local", ScheduledTaskAction.Enable));

        var result = await Controller(config, groups).Execute(
            new ScheduledTasksController.ActionRequest { Server = "SRV01", Path = @"\minicon\export", Action = ScheduledTaskAction.Enable },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ScheduledTaskActionResult>().Which.Success.Should().BeTrue();
        _control.Verify(c => c.ExecuteAsync("srv01.dom.local", TaskPath, ScheduledTaskAction.Enable, @"DOM\alice", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Action_WithoutAuthenticatedUser_UsesWebUiAsUser()
    {
        _control.Setup(c => c.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.Disable, "WebUI", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok("srv02", ScheduledTaskAction.Disable));

        var result = await Controller(DefaultConfig(), user: null).Execute(
            new ScheduledTasksController.ActionRequest { Server = "srv02", Path = TaskPath, Action = ScheduledTaskAction.Disable },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _control.Verify(c => c.ExecuteAsync("srv02", TaskPath, ScheduledTaskAction.Disable, "WebUI", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Live_UnknownServerOfTheGroup_IsAllowed()
    {
        _control.Setup(c => c.QueryAsync("srv03", TaskPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduledTaskLiveState("srv03", true, new ScheduledTaskInfo { Path = TaskPath, Enabled = false }, null));

        var result = await Controller(DefaultConfig()).Live("srv03", TaskPath, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ScheduledTaskLiveState>().Which.Server.Should().Be("srv03");
    }

    // ── Activate exclusively ────────────────────────────────────────────────

    [Fact]
    public async Task ActivateExclusive_PassesServerSideGroupServersIncludingUnknown()
    {
        IReadOnlyList<string>? passed = null;
        IReadOnlyCollection<string>? reporting = null;
        _control.Setup(c => c.ActivateExclusivelyAsync(TaskPath, "srv02", It.IsAny<IReadOnlyList<string>>(), @"DOM\alice", It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, string, IReadOnlyCollection<string>?, CancellationToken>((_, _, servers, _, rep, _) => { passed = servers; reporting = rep; })
            .ReturnsAsync(new ScheduledTaskExclusiveResult(TaskPath, "srv02", true, Array.Empty<ScheduledTaskActionResult>(), "OK"));

        var result = await Controller(DefaultConfig()).ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = TaskPath, TargetServer = "srv02" }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        passed.Should().BeEquivalentTo(new[] { "srv01", "srv02", "srv03" }, o => o.WithStrictOrdering());
        reporting.Should().BeEquivalentTo(new[] { "srv01", "srv02" }); // unknown srv03 did not report the task
    }

    [Fact]
    public async Task ActivateExclusive_TargetOutsideTheGroup_Returns404WithoutCall()
    {
        var result = await Controller(DefaultConfig()).ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = TaskPath, TargetServer = "srv-noTasks" }, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        _control.Verify(c => c.ActivateExclusivelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateExclusive_GroupInOtherEnvironment_IsNotMixedIn()
    {
        var groups = new[]
        {
            new TaskGroup("production", TaskPath, ScheduledTaskMode.SingleActive, new[] { Member("srv01", true) }, Array.Empty<string>(), "srv01"),
            new TaskGroup("ref", TaskPath, ScheduledTaskMode.SingleActive, new[] { Member("srv02", true), Member("srv03", false) }, Array.Empty<string>(), "srv02")
        };
        IReadOnlyList<string>? passed = null;
        _control.Setup(c => c.ActivateExclusivelyAsync(TaskPath, "srv03", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, string, IReadOnlyCollection<string>?, CancellationToken>((_, _, servers, _, _, _) => passed = servers)
            .ReturnsAsync(new ScheduledTaskExclusiveResult(TaskPath, "srv03", true, Array.Empty<ScheduledTaskActionResult>(), "OK"));

        await Controller(DefaultConfig(), groups).ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = TaskPath, TargetServer = "srv03" }, CancellationToken.None);

        passed.Should().BeEquivalentTo(new[] { "srv02", "srv03" });
    }

    // ── Ignore rules ────────────────────────────────────────────────────────

    private ScheduledTasksIgnoreController IgnoreController(ConfigurationService config, string? user = @"DOM\bob")
    {
        var reader = new StatusReaderService(_dir, Mock.Of<IAcknowledgeService>(), config, NullLogger<StatusReaderService>.Instance);
        var controller = new ScheduledTasksIgnoreController(config, reader);
        var identity = user == null ? new ClaimsIdentity() : new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "Negotiate");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    [Fact]
    public async Task Ignore_CreatesServerScopedRuleWithUserAndDuration()
    {
        var config = DefaultConfig();
        var before = DateTime.UtcNow;

        var result = await IgnoreController(config).Create(new CreateScheduledTaskIgnoreRequest
        {
            ServerId = "SRV02", TaskPath = TaskPath, EventId = 101, DurationDays = 7, Reason = "  Ticket 4711  "
        });

        result.Should().BeOfType<OkObjectResult>();
        var rule = config.GetFeatures().ScheduledTasks.IgnoreRules.Should().ContainSingle().Subject;
        rule.Servers.Should().Equal("srv02");
        rule.TaskPath.Should().Be(TaskPath);
        rule.EventIds.Should().Equal(101);
        rule.Reason.Should().Be("Ticket 4711");
        rule.CreatedBy.Should().Be(@"DOM\bob");
        rule.ExpiresAt.Should().BeCloseTo(before.AddDays(7), TimeSpan.FromMinutes(1));

        // Persisted to config.json
        File.ReadAllText(Path.Combine(_dir, "config.json")).Should().Contain("Ticket 4711");
    }

    [Fact]
    public async Task Ignore_ExplicitDateIsStoredAsUtc_PermanentWithoutDate()
    {
        var config = DefaultConfig();
        var until = DateTime.UtcNow.AddDays(3);

        await IgnoreController(config).Create(new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", EventId = 203, ExpiresAt = until, Reason = "bis Freitag" });
        await IgnoreController(config, user: null).Create(new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", TaskPath = TaskPath, Reason = "dauerhaft" });

        var rules = config.GetFeatures().ScheduledTasks.IgnoreRules;
        rules.Should().HaveCount(2);
        rules[0].ExpiresAt.Should().Be(until);
        rules[0].ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        rules[0].TaskPath.Should().BeNull();
        rules[1].ExpiresAt.Should().BeNull();
        rules[1].CreatedBy.Should().Be("WebUI");
    }

    public static TheoryData<CreateScheduledTaskIgnoreRequest> InvalidIgnoreRequests() => new()
    {
        new CreateScheduledTaskIgnoreRequest { ServerId = "", TaskPath = TaskPath, Reason = "x" },                                  // no server
        new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", Reason = "x" },                                                  // no criterion
        new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", TaskPath = TaskPath, Reason = " " },                             // no reason
        new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", TaskPath = TaskPath, Reason = "x", DurationDays = 14 },          // not 1/7/30/90
        new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", TaskPath = TaskPath, Reason = "x", ExpiresAt = DateTime.UtcNow.AddMinutes(-1) },
        new CreateScheduledTaskIgnoreRequest { ServerId = "srv01", TaskPath = TaskPath, Reason = "x", DurationDays = 7, ExpiresAt = DateTime.UtcNow.AddDays(1) }
    };

    [Theory]
    [MemberData(nameof(InvalidIgnoreRequests))]
    public async Task Ignore_InvalidRequest_Returns400AndAddsNothing(CreateScheduledTaskIgnoreRequest request)
    {
        var config = DefaultConfig();

        var result = await IgnoreController(config).Create(request);

        result.Should().BeOfType<BadRequestObjectResult>();
        config.GetFeatures().ScheduledTasks.IgnoreRules.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignore_UnknownServer_Returns404()
    {
        var config = DefaultConfig();

        var result = await IgnoreController(config).Create(new CreateScheduledTaskIgnoreRequest { ServerId = "srv99", EventId = 101, Reason = "x" });

        result.Should().BeOfType<NotFoundObjectResult>();
        config.GetFeatures().ScheduledTasks.IgnoreRules.Should().BeEmpty();
    }

    // ── Servers/Edit form binding ───────────────────────────────────────────

    [Fact]
    public void WatchRows_EmptyRowsAreDropped_TextFieldsAreParsed()
    {
        var vm = new ScheduledTaskChecksViewModel
        {
            Enabled = true,
            CheckEvents = false,
            Tasks = new List<ScheduledTaskWatchRowViewModel>
            {
                new()
                {
                    Pattern = @" \Minicon\* ", ExcludePatterns = @"\Minicon\Test*; \Minicon\Old", Mode = ScheduledTaskMode.AllServers,
                    MaxRuntimeMinutes = 30, AllowedResultCodes = "1, 0x41306", FailureIsCritical = false, CheckMissedRun = false,
                    WikiUrl = " https://wiki/x ", RemediationSteps = "Schritt 1\r\n\r\nSchritt 2"
                },
                new() { Pattern = "   ", WikiUrl = "https://wiki/ignored" },
                new()
            }
        };

        var config = vm.ToServerScheduledTasksConfig();

        config.Should().NotBeNull();
        config!.Enabled.Should().BeTrue();
        config.CheckEvents.Should().BeFalse();
        var watch = config.Tasks.Should().ContainSingle().Subject;
        watch.Pattern.Should().Be(@"\Minicon\*");
        watch.ExcludePatterns.Should().Equal(@"\Minicon\Test*", @"\Minicon\Old");
        watch.Mode.Should().Be(ScheduledTaskMode.AllServers);
        watch.MaxRuntimeMinutes.Should().Be(30);
        watch.AllowedResultCodes.Should().Equal(1, 0x41306);
        watch.FailureIsCritical.Should().BeFalse();
        watch.CheckMissedRun.Should().BeFalse();
        watch.WikiUrl.Should().Be("https://wiki/x");
        watch.RemediationSteps.Should().Equal("Schritt 1", "Schritt 2");
    }

    [Fact]
    public void WatchRows_RoundTripThroughViewModel()
    {
        var original = new ServerScheduledTasksConfig
        {
            Enabled = true,
            Tasks = { new ScheduledTaskWatchConfig { Pattern = @"\Export*", AllowedResultCodes = { unchecked((int)0x800710E0) }, RemediationSteps = { "a", "b" } } }
        };

        var roundTrip = ScheduledTaskChecksViewModel.FromServerScheduledTasksConfig(original).ToServerScheduledTasksConfig();

        roundTrip!.Tasks.Should().ContainSingle().Which.AllowedResultCodes.Should().Equal(unchecked((int)0x800710E0));
        roundTrip.Tasks[0].RemediationSteps.Should().Equal("a", "b");
    }

    [Fact]
    public void WatchRows_DisabledWithoutRows_IsNull_DisabledWithRows_KeepsRows()
    {
        new ScheduledTaskChecksViewModel { Enabled = false, Tasks = { new() } }.ToServerScheduledTasksConfig().Should().BeNull();

        var kept = new ScheduledTaskChecksViewModel { Enabled = false, Tasks = { new() { Pattern = @"\A" } } }.ToServerScheduledTasksConfig();
        kept!.Enabled.Should().BeFalse();
        kept.Tasks.Should().ContainSingle();
    }

    [Fact]
    public void ServerChecks_IncludeScheduledTasks()
    {
        var checks = new ServerChecksViewModel
        {
            ScheduledTasks = new ScheduledTaskChecksViewModel { Enabled = true, Tasks = { new() { Pattern = @"\Minicon\*" } } }
        }.ToServerChecksConfig();

        checks!.ScheduledTasks!.Tasks.Should().ContainSingle().Which.Pattern.Should().Be(@"\Minicon\*");
        ServerChecksViewModel.FromServerChecksConfig(checks).ScheduledTasks.Tasks.Should().ContainSingle();
    }

    // ── Group finding (shared by page + API) ────────────────────────────────

    [Fact]
    public void Violation_FollowsSingleActiveRule()
    {
        ScheduledTaskTargets.Violation(DefaultGroups()[0]).Should().Be(ScheduledTaskViolation.MultipleActive);

        var none = new TaskGroup("p", TaskPath, ScheduledTaskMode.SingleActive, new[] { Member("srv01", false) }, Array.Empty<string>(), "srv01");
        ScheduledTaskTargets.Violation(none).Should().Be(ScheduledTaskViolation.NoneActive);

        var unsure = none with { UnknownServers = new[] { "srv03" } };
        ScheduledTaskTargets.Violation(unsure).Should().Be(ScheduledTaskViolation.PossiblyNoneActive);

        var all = none with { Mode = ScheduledTaskMode.AllServers };
        ScheduledTaskTargets.Violation(all).Should().Be(ScheduledTaskViolation.None);
    }

    // ── Feature off, audit, JSON-only POSTs, safe links ─────────────────────

    [Fact]
    public async Task FeatureOff_GetReturnsNoGroups_ActionIs400WithoutCall()
    {
        var config = LoadConfig(new Config { Servers = new List<Server> { TaskServer("srv01") } });
        var controller = Controller(config);

        var get = await controller.Get();
        var action = await controller.Execute(
            new ScheduledTasksController.ActionRequest { Server = "srv01", Path = TaskPath, Action = ScheduledTaskAction.Run }, CancellationToken.None);

        var json = JsonSerializer.Serialize(get.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain("\"featureEnabled\":false").And.Contain("\"groups\":[]");
        action.Should().BeOfType<BadRequestObjectResult>();
        _control.Verify(c => c.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScheduledTaskAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Get_ReportsViolationPerGroup()
    {
        var json = JsonSerializer.Serialize((await Controller(DefaultConfig()).Get()).Should().BeOfType<OkObjectResult>().Subject.Value);

        json.Should().Contain("\"violation\":\"MultipleActive\"").And.Contain("\"controlEnabled\":true");
    }

    [Fact]
    public void Audit_PassesTrimmedPathAndClampedMax()
    {
        var entries = new[] { new ScheduledTaskAuditEntry { User = @"DOM\alice", Server = "srv01", TaskPath = TaskPath, Action = "Disable", Success = true } };
        _control.Setup(c => c.GetAuditLog(1000, TaskPath)).Returns(entries);
        _control.Setup(c => c.GetAuditLog(200, null)).Returns(Array.Empty<ScheduledTaskAuditEntry>());
        var controller = Controller(DefaultConfig());

        controller.Audit("  " + TaskPath + " ", 5000).Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(entries);
        controller.Audit(null).Should().BeOfType<OkObjectResult>();
        _control.Verify(c => c.GetAuditLog(1000, TaskPath), Times.Once);
        _control.Verify(c => c.GetAuditLog(200, null), Times.Once);
    }

    [Theory]
    [InlineData(typeof(ScheduledTasksController))]
    [InlineData(typeof(ScheduledTasksIgnoreController))]
    public void AllPosts_AcceptJsonOnly(Type controller)
    {
        var posts = controller.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), false).Length > 0)
            .ToList();

        posts.Should().NotBeEmpty();
        foreach (var post in posts)
        {
            var consumes = post.GetCustomAttributes(typeof(ConsumesAttribute), false).Cast<ConsumesAttribute>().SingleOrDefault();
            consumes.Should().NotBeNull($"{controller.Name}.{post.Name} must only accept JSON");
            consumes!.ContentTypes.Should().Equal("application/json");
        }
    }

    [Theory]
    [InlineData("https://wiki.intern/x", "https://wiki.intern/x")]
    [InlineData("HTTP://wiki/x", "HTTP://wiki/x")]
    [InlineData("/wiki/x?y=1", "/wiki/x?y=1")]
    [InlineData("docs/run.html#a:b", "docs/run.html#a:b")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData(" JavaScript:alert(1)", null)]
    [InlineData("data:text/html,x", null)]
    [InlineData("//evil.example.com", null)]
    [InlineData("/\\evil.example.com", null)]
    [InlineData("mailto:betrieb@example.org", "mailto:betrieb@example.org")]
    [InlineData("file:///c:/x", null)]
    [InlineData("\\\\srv\\wiki\\task.docx", null)]
    [InlineData("  ", null)]
    public void SafeHref_AllowsOnlyHttpAndRelative(string url, string? expected)
    {
        Minicon.SimpleAdmin.WebUI.Helpers.Display.SafeHref(url).Should().Be(expected);
    }

    [Fact]
    public async Task ActivateExclusive_MemberNoLongerMonitoredInConfig_Returns409WithoutCall()
    {
        // srv02 still reports the task (status data), but is inactive in config.json → it could not be disabled.
        var config = LoadConfig(new Config
        {
            Features = new FeaturesConfig { ScheduledTasks = new ScheduledTaskFeatureConfig { Enabled = true } },
            Servers = new List<Server> { TaskServer("srv01"), new() { Name = "srv02", Active = false, Checks = TaskServer("srv02").Checks }, TaskServer("srv03") }
        });

        var result = await Controller(config).ActivateExclusive(
            new ScheduledTasksController.ExclusiveRequest { Path = TaskPath, TargetServer = "srv01" }, CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain("srv02");
        _control.Verify(c => c.ActivateExclusivelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
