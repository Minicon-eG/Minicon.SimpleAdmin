using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// config.json round trip of the scheduled task configuration through the real readers/writers: worker
/// (<see cref="ServiceConfigReader"/>), WebUI (<see cref="ConfigurationService"/>) and the WebUI save paths
/// (Settings → Features, Servers/Edit). Also: a config.json without the block keeps working.
/// </summary>
public sealed class ScheduledTaskConfigRoundtripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", "schtask-config-" + Guid.NewGuid().ToString("N"));

    public ScheduledTaskConfigRoundtripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>Hand-written config.json: every scheduled task field set, the mode once as string and once as number.</summary>
    private const string FullConfig = """
        {
          // comment like in real hand-edited files
          "environments": { "production": { "services": ["srv01", "srv02"] } },
          "features": {
            "scheduledTasks": {
              "enabled": true,
              "checkIntervalSeconds": 300,
              "defaults": {
                "maxRuntimeMinutes": 90,
                "missedRunGraceMinutes": 15,
                "allowedResultCodes": [1, 267009, -2147020576],
                "eventTimeWindowMinutes": 120,
                "eventWarningCount": 2,
                "eventCriticalCount": 7,
                "maxReportedEvents": 25
              },
              "ignoreRules": [
                {
                  "taskPath": "\\Minicon\\Export",
                  "eventIds": [101, 103],
                  "messageContains": "Zeitüberschreitung",
                  "servers": ["srv01", "srv0*"],
                  "reason": "bekanntes Rauschen",
                  "createdBy": "DOM\\alice",
                  "expiresAt": "2027-03-01T14:30:00Z"
                }
              ]
            }
          },
          "servers": [
            {
              "name": "srv01",
              "active": true,
              "serviceTypes": [ { "type": "api" } ],
              "checks": {
                "scheduledTasks": {
                  "enabled": true,
                  "checkEvents": false,
                  "tasks": [
                    {
                      "pattern": "\\Minicon\\*",
                      "excludePatterns": ["\\Minicon\\Test*", "\\Minicon\\Alt"],
                      "mode": "AllServers",
                      "maxRuntimeMinutes": 0,
                      "missedRunGraceMinutes": 45,
                      "checkMissedRun": false,
                      "allowedResultCodes": [2, -2147024891],
                      "failureIsCritical": false,
                      "wikiUrl": "https://wiki/export",
                      "remediationSteps": ["Konto prüfen", "Task neu starten"]
                    },
                    { "pattern": "Export*", "mode": 0 }
                  ]
                }
              }
            },
            { "name": "srv02", "active": true, "serviceTypes": [ { "type": "api" } ] },
          ]
        }
        """;

    private const string ConfigWithoutBlock = """
        {
          "environments": { "production": { "services": ["srv01"] } },
          "features": { "eventLog": { "enabled": true } },
          "servers": [ { "name": "srv01", "active": true, "serviceTypes": [ { "type": "api" } ], "checks": { "services": { "enabled": true, "checks": [ { "name": "W3SVC" } ] } } } ]
        }
        """;

    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static async Task<Config> WorkerLoadAsync(string json)
    {
        var fs = new MockFileSystem();
        fs.AddFile("/cfg/config.json", new MockFileData(json));
        return await new ServiceConfigReader("/cfg/config.json", new Mock<ILogger<ServiceConfigReader>>().Object, fs).LoadConfigAsync();
    }

    private ConfigurationService WebUiLoad(string json)
    {
        File.WriteAllText(ConfigPath, json);
        var service = new ConfigurationService(new ConfigurationBuilder().Build());
        // Unlike the worker, the WebUI parser accepts no comments/trailing commas (pre-existing) — normalise first.
        var normalised = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.ToJsonString();
        service.LoadFromFile(normalised, ConfigPath);
        return service;
    }

    private static string ScheduledTasksJson(Config config) => JsonSerializer.Serialize(new
    {
        global = config.GetEffectiveFeatures().ScheduledTasks,
        servers = config.Servers.Select(s => new { s.Name, s.Checks?.ScheduledTasks })
    }, Canonical);

    private static void AssertFullConfig(Config config)
    {
        var global = config.GetEffectiveFeatures().ScheduledTasks;
        global.Enabled.Should().BeTrue();
        global.CheckIntervalSeconds.Should().Be(300);
        global.Defaults.MaxRuntimeMinutes.Should().Be(90);
        global.Defaults.MissedRunGraceMinutes.Should().Be(15);
        global.Defaults.AllowedResultCodes.Should().Equal(1, 0x41301, unchecked((int)0x800710E0));
        global.Defaults.EventTimeWindowMinutes.Should().Be(120);
        global.Defaults.EventWarningCount.Should().Be(2);
        global.Defaults.EventCriticalCount.Should().Be(7);
        global.Defaults.MaxReportedEvents.Should().Be(25);

        var rule = global.IgnoreRules.Should().ContainSingle().Subject;
        rule.TaskPath.Should().Be(@"\Minicon\Export");
        rule.EventIds.Should().Equal(101, 103);
        rule.MessageContains.Should().Be("Zeitüberschreitung");
        rule.Servers.Should().Equal("srv01", "srv0*");
        rule.Reason.Should().Be("bekanntes Rauschen");
        rule.CreatedBy.Should().Be(@"DOM\alice");
        rule.ExpiresAt!.Value.ToUniversalTime().Should().Be(new DateTime(2027, 3, 1, 14, 30, 0, DateTimeKind.Utc));

        var server = config.Servers.Single(s => s.Name == "srv01").Checks!.ScheduledTasks!;
        server.Enabled.Should().BeTrue();
        server.CheckEvents.Should().BeFalse();
        server.Tasks.Should().HaveCount(2);
        var first = server.Tasks[0];
        first.Pattern.Should().Be(@"\Minicon\*");
        first.ExcludePatterns.Should().Equal(@"\Minicon\Test*", @"\Minicon\Alt");
        first.Mode.Should().Be(ScheduledTaskMode.AllServers);
        first.MaxRuntimeMinutes.Should().Be(0);
        first.MissedRunGraceMinutes.Should().Be(45);
        first.CheckMissedRun.Should().BeFalse();
        first.AllowedResultCodes.Should().Equal(2, unchecked((int)0x80070005));
        first.FailureIsCritical.Should().BeFalse();
        first.WikiUrl.Should().Be("https://wiki/export");
        first.RemediationSteps.Should().Equal("Konto prüfen", "Task neu starten");

        var second = server.Tasks[1];
        second.Pattern.Should().Be("Export*");
        second.Mode.Should().Be(ScheduledTaskMode.SingleActive);
        second.MaxRuntimeMinutes.Should().BeNull();
        second.CheckMissedRun.Should().BeTrue();
        second.FailureIsCritical.Should().BeTrue();

        config.Servers.Single(s => s.Name == "srv02").Checks?.ScheduledTasks.Should().BeNull();
        ScheduledTaskGroupEvaluator.IsParticipant(config.GetEffectiveFeatures(), config.Servers[0]).Should().BeTrue();
        ScheduledTaskGroupEvaluator.IsParticipant(config.GetEffectiveFeatures(), config.Servers[1]).Should().BeFalse();
    }

    // ── Full block ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FullBlock_IsReadCompletely_ByWorkerAndWebUi()
    {
        AssertFullConfig(await WorkerLoadAsync(FullConfig));
        AssertFullConfig(WebUiLoad(FullConfig).Config);
    }

    [Fact]
    public async Task FullBlock_WebUiSave_AndReload_IsIdentical_AndWorkerReadsTheSavedFile()
    {
        var service = WebUiLoad(FullConfig);
        var before = ScheduledTasksJson(service.Config);

        await service.SaveToFileAsync();
        var savedJson = await File.ReadAllTextAsync(ConfigPath);
        await service.ReloadFromFileAsync();

        ScheduledTasksJson(service.Config).Should().Be(before);
        service.HasChanges.Should().BeFalse();
        AssertFullConfig(service.Config);

        var worker = await WorkerLoadAsync(savedJson);
        ScheduledTasksJson(worker).Should().Be(before);

        // Form-binding helpers must not leak into the file; the mode is written as a string.
        savedJson.Should().NotContain("excludePatternsText").And.NotContain("allowedResultCodesText")
            .And.NotContain("eventIdsText").And.NotContain("serversText");
        savedJson.Should().Contain("\"mode\": \"AllServers\"").And.Contain("\"mode\": \"SingleActive\"");
    }

    [Fact]
    public async Task FullBlock_SettingsFeaturesSave_KeepsEveryGlobalField()
    {
        var service = WebUiLoad(FullConfig);
        var before = ScheduledTasksJson(service.Config);
        var model = new FeaturesViewModel
        {
            // what the form posts: everything except the check interval, the codes as text field
            Features = JsonSerializer.Deserialize<FeaturesConfig>(JsonSerializer.Serialize(service.GetFeatures(), Canonical), Canonical)!,
            ScheduledTaskAllowedResultCodes = new ScheduledTaskWatchConfig
            {
                AllowedResultCodes = service.GetFeatures().ScheduledTasks.Defaults.AllowedResultCodes
            }.AllowedResultCodesText
        };
        model.Features.ScheduledTasks.CheckIntervalSeconds = null;
        model.Features.ScheduledTasks.Defaults.AllowedResultCodes = new List<int>();

        await Controller(service).Features(model);

        var reloaded = await WorkerLoadAsync(await File.ReadAllTextAsync(ConfigPath));
        ScheduledTasksJson(reloaded).Should().Be(before);
    }

    [Fact]
    public async Task FullBlock_ServersEditSave_KeepsEveryServerField()
    {
        var service = WebUiLoad(FullConfig);
        var before = ScheduledTasksJson(service.Config);
        var model = ServerViewModel.FromServer(service.GetServer("srv01")!);

        var controller = new ServersController(service, new Mock<ILogger<ServersController>>().Object)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };
        await controller.Edit("srv01", model); // Edit stays on the page (ViewResult) also on success

        controller.ModelState.IsValid.Should().BeTrue();
        controller.TempData["Success"].Should().NotBeNull();
        var reloaded = await WorkerLoadAsync(await File.ReadAllTextAsync(ConfigPath));
        ScheduledTasksJson(reloaded).Should().Be(before);
    }

    // ── Missing block / old config.json ──────────────────────────────────────

    [Fact]
    public async Task MissingBlock_FeatureOff_NoException_NoParticipants()
    {
        foreach (var config in new[] { await WorkerLoadAsync(ConfigWithoutBlock), WebUiLoad(ConfigWithoutBlock).Config })
        {
            var features = config.GetEffectiveFeatures();
            features.ScheduledTasks.Should().NotBeNull();
            features.ScheduledTasks.Enabled.Should().BeFalse();
            features.ScheduledTasks.IgnoreRules.Should().BeEmpty();
            config.Servers[0].Checks!.ScheduledTasks.Should().BeNull();
            ScheduledTaskGroupEvaluator.IsParticipant(features, config.Servers[0]).Should().BeFalse();
            ScheduledTaskGroupEvaluator.BuildGroups(config, Array.Empty<ScheduledTaskGroupEvaluator.ServerSnapshot>()).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MissingFeaturesSection_FeatureOff_NoException()
    {
        const string json = """{ "servers": [ { "name": "srv01", "serviceTypes": [ { "type": "api" } ] } ] }""";
        var config = await WorkerLoadAsync(json);

        config.GetEffectiveFeatures().ScheduledTasks.Enabled.Should().BeFalse();
        ScheduledTaskGroupEvaluator.GetStaleAfter(config).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task OldConfig_SettingsFeaturesSave_KeepsFeatureOff_AndOtherSettings()
    {
        var service = WebUiLoad(ConfigWithoutBlock);
        // An old form (or one without the scheduled task card) posts nothing for scheduledTasks.
        var model = new FeaturesViewModel
        {
            Features = JsonSerializer.Deserialize<FeaturesConfig>(JsonSerializer.Serialize(service.GetFeatures(), Canonical), Canonical)!
        };
        model.Features.ScheduledTasks = null!;

        var result = await Controller(service).Features(model);

        result.Should().BeOfType<RedirectToActionResult>();
        var reloaded = await WorkerLoadAsync(await File.ReadAllTextAsync(ConfigPath));
        reloaded.GetEffectiveFeatures().ScheduledTasks.Enabled.Should().BeFalse();
        reloaded.GetEffectiveFeatures().ScheduledTasks.IgnoreRules.Should().BeEmpty();
        reloaded.GetEffectiveFeatures().EventLog.Enabled.Should().BeTrue();
        reloaded.Servers[0].Checks!.Services!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task OldConfig_ServersEditSave_DoesNotAddAScheduledTaskBlock()
    {
        var service = WebUiLoad(ConfigWithoutBlock);
        var model = ServerViewModel.FromServer(service.GetServer("srv01")!);
        var controller = new ServersController(service, new Mock<ILogger<ServersController>>().Object)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };

        await controller.Edit("srv01", model);

        controller.TempData["Success"].Should().NotBeNull();
        var saved = await File.ReadAllTextAsync(ConfigPath);
        var server = JsonNode.Parse(saved)!["servers"]![0]!;
        server["checks"]?["scheduledTasks"].Should().BeNull();
        (await WorkerLoadAsync(saved)).Servers[0].Checks?.ScheduledTasks.Should().BeNull();
    }

    [Fact]
    public async Task ServerCardSwitchedOff_KeepsRowsDisabled_SoTheWorkerSkipsIt()
    {
        var service = WebUiLoad(FullConfig);
        var model = ServerViewModel.FromServer(service.GetServer("srv01")!);
        model.Checks.ScheduledTasks.Enabled = false;

        await new ServersController(service, new Mock<ILogger<ServersController>>().Object)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        }.Edit("srv01", model);

        var reloaded = await WorkerLoadAsync(await File.ReadAllTextAsync(ConfigPath));
        var cfg = reloaded.Servers.Single(s => s.Name == "srv01").Checks!.ScheduledTasks!;
        cfg.Enabled.Should().BeFalse();
        cfg.Tasks.Should().HaveCount(2);
        ScheduledTaskGroupEvaluator.IsParticipant(reloaded.GetEffectiveFeatures(), reloaded.Servers[0]).Should().BeFalse();
    }

    [Theory]
    [InlineData("\"allServers\"")]
    [InlineData("\"ALLSERVERS\"")]
    [InlineData("1")]
    public async Task Mode_AcceptsCamelCaseUppercaseAndNumber(string literal)
    {
        var json = $$"""
            {
              "servers": [ { "name": "srv01", "serviceTypes": [ { "type": "api" } ],
                "checks": { "scheduledTasks": { "enabled": true, "tasks": [ { "pattern": "x", "mode": {{literal}} } ] } } } ]
            }
            """;

        (await WorkerLoadAsync(json)).Servers[0].Checks!.ScheduledTasks!.Tasks[0].Mode.Should().Be(ScheduledTaskMode.AllServers);
        WebUiLoad(json).Config.Servers[0].Checks!.ScheduledTasks!.Tasks[0].Mode.Should().Be(ScheduledTaskMode.AllServers);
    }

    private static async Task<Config> LoadWithBlocksAsync(string globalBlock, string serverBlock) => await WorkerLoadAsync($$"""
        {
          "features": { {{globalBlock}} },
          "servers": [ { "name": "srv01", "active": true, "serviceTypes": [ { "type": "api" } ], "checks": { "scheduledTasks": {{serverBlock}} } } ]
        }
        """);

    private static void CentralEvaluation(Config config)
    {
        ScheduledTaskGroupEvaluator.GetStaleAfter(config);
        var groups = ScheduledTaskGroupEvaluator.BuildGroups(config, Array.Empty<ScheduledTaskGroupEvaluator.ServerSnapshot>());
        ScheduledTaskGroupEvaluator.DeriveGroupProblems(groups);
        var features = config.GetEffectiveFeatures().ScheduledTasks;
        ScheduledTaskIgnoreMatcher.ApplyCentral(new Models.State.ScheduledTaskState(), "srv01", features?.IgnoreRules, features?.Defaults, DateTime.UtcNow);
    }

    private const string ServerWithPattern = "{ \"enabled\": true, \"tasks\": [ { \"pattern\": \"x\" } ] }";

    /// <summary>Hand-edited config.json with explicit nulls: the models replace them by their defaults on load.</summary>
    [Theory]
    [InlineData("\"scheduledTasks\": { \"enabled\": true, \"ignoreRules\": null }", ServerWithPattern)]
    [InlineData("\"scheduledTasks\": { \"enabled\": true, \"defaults\": null }", ServerWithPattern)]
    public async Task ExplicitNestedNulls_DoNotBreakTheCentralEvaluation(string globalBlock, string serverBlock)
    {
        var config = await LoadWithBlocksAsync(globalBlock, serverBlock);
        var features = config.GetEffectiveFeatures().ScheduledTasks;
        features.IgnoreRules.Should().NotBeNull("an explicit null is replaced by the default on load");
        features.Defaults.Should().NotBeNull("an explicit null is replaced by the default on load");

        FluentActions.Invoking(() => CentralEvaluation(config)).Should().NotThrow();
    }

    [Fact]
    public async Task ExplicitNullFeatureBlock_DoesNotBreakTheCentralEvaluation()
    {
        var config = await LoadWithBlocksAsync("\"scheduledTasks\": null", ServerWithPattern);
        config.GetEffectiveFeatures().ScheduledTasks.Should().NotBeNull("an explicit null is replaced by the default (feature off) on load");
        config.GetEffectiveFeatures().ScheduledTasks.Enabled.Should().BeFalse();

        FluentActions.Invoking(() => CentralEvaluation(config)).Should().NotThrow();
    }

    [Fact]
    public async Task ExplicitNullTaskList_DoesNotBreakTheCentralEvaluation()
    {
        var config = await LoadWithBlocksAsync("\"scheduledTasks\": { \"enabled\": true }", "{ \"enabled\": true, \"tasks\": null }");
        config.Servers[0].Checks!.ScheduledTasks!.Tasks.Should().BeEmpty("an explicit null is replaced by an empty list on load");

        FluentActions.Invoking(() => CentralEvaluation(config)).Should().NotThrow();
    }

    private static SettingsController Controller(ConfigurationService service) => new(service)
    {
        TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
    };
}
