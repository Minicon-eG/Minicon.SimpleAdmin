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
/// config.json round trip of the "offene Läufe" (pending run) configuration through the real
/// readers/writers: worker (<see cref="ServiceConfigReader"/>), WebUI (<see cref="ConfigurationService"/>)
/// and the WebUI save path (Servers/Edit). Also: a config.json without the block keeps working.
/// </summary>
public sealed class LogPendingRuleConfigRoundtripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", "logrun-config-" + Guid.NewGuid().ToString("N"));

    public LogPendingRuleConfigRoundtripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>Hand-written config.json with a log scan that has pending rules.</summary>
    private const string FullConfig = """
        {
          // comment like in real hand-edited files
          "environments": { "production": { "services": ["srv01"] } },
          "features": { "fileMonitoring": { "enabled": true } },
          "servers": [
            {
              "name": "srv01",
              "active": true,
              "serviceTypes": [ { "type": "api" } ],
              "checks": {
                "fileMonitoring": {
                  "enabled": true,
                  "logScans": [
                    {
                      "name": "Schnittstellen-Log",
                      "path": "\\\\srv\\d$\\Daten\\schnittstelle\\kasse\\log\\*.log",
                      "maxLines": 5000,
                      "patterns": [
                        { "name": "Hängende Nachricht", "severity": "Critical", "regex": "Nachricht.*haengt" }
                      ],
                      "pendingRules": [
                        {
                          "name": "Lauf muss abgeschlossen sein",
                          "startRegex": "waiting for Result",
                          "endRegex": "Verarbeitungsergebnis:",
                          "abortRegex": "Verarbeitung abgebrochen",
                          "maxMinutes": 45,
                          "severity": "Warning",
                          "reportAbandoned": false,
                          "timestampRegex": "\\[(\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2})\\]",
                          "timestampFormat": "yyyy-MM-dd HH:mm:ss",
                          "wikiUrl": "https://wiki.intern/lauf",
                          "remediationSteps": [ "Job prüfen", "Ergebnis nachliefern" ]
                        },
                        {
                          "name": "Standard",
                          "startRegex": "waiting for Result",
                          "endRegex": "Verarbeitungsergebnis:"
                        }
                      ]
                    }
                  ]
                }
              }
            }
          ]
        }
        """;

    private const string ConfigWithoutBlock = """
        {
          "environments": { "production": { "services": ["srv01"] } },
          "servers": [ { "name": "srv01", "active": true, "serviceTypes": [ { "type": "api" } ],
            "checks": { "fileMonitoring": { "enabled": true, "logScans": [ { "name": "L", "path": "/log/a.log" } ] } } } ]
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
        var normalised = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.ToJsonString();
        service.LoadFromFile(normalised, ConfigPath);
        return service;
    }

    private static string PendingRulesJson(Config config) => JsonSerializer.Serialize(new
    {
        scans = config.Servers.SelectMany(s => s.Checks?.FileMonitoring?.LogScans ?? new List<LogScanConfig>())
            .Select(scan => new { scan.Name, scan.PendingRules })
    }, Canonical);

    private static void AssertFullConfig(Config config)
    {
        var scan = config.Servers.Single(s => s.Name == "srv01").Checks!.FileMonitoring!.LogScans.Should().ContainSingle().Subject;
        scan.Patterns.Should().ContainSingle();

        scan.PendingRules.Should().HaveCount(2);
        var rules = scan.PendingRules.ToList();
        var first = rules[0];
        first.Name.Should().Be("Lauf muss abgeschlossen sein");
        first.StartRegex.Should().Be("waiting for Result");
        first.EndRegex.Should().Be("Verarbeitungsergebnis:");
        first.AbortRegex.Should().Be("Verarbeitung abgebrochen");
        first.MaxMinutes.Should().Be(45);
        first.Severity.Should().Be("Warning");
        first.ReportAbandoned.Should().BeFalse();
        first.TimestampRegex.Should().Be(@"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]");
        first.TimestampFormat.Should().Be("yyyy-MM-dd HH:mm:ss");
        first.WikiUrl.Should().Be("https://wiki.intern/lauf");
        first.RemediationSteps.Should().Equal("Job prüfen", "Ergebnis nachliefern");

        // Defaults for the second rule.
        var second = rules[1];
        second.MaxMinutes.Should().Be(30);
        second.Severity.Should().Be("Critical");
        second.ReportAbandoned.Should().BeTrue();
        second.TimestampFormat.Should().Be("yyyy-MM-dd HH:mm:ss.fff");
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
        var before = PendingRulesJson(service.Config);

        await service.SaveToFileAsync();
        var savedJson = await File.ReadAllTextAsync(ConfigPath);
        await service.ReloadFromFileAsync();

        PendingRulesJson(service.Config).Should().Be(before);
        service.HasChanges.Should().BeFalse();
        AssertFullConfig(service.Config);

        var worker = await WorkerLoadAsync(savedJson);
        PendingRulesJson(worker).Should().Be(before);
    }

    [Fact]
    public async Task FullBlock_ServersEditSave_KeepsEveryPendingRule()
    {
        var service = WebUiLoad(FullConfig);
        var before = PendingRulesJson(service.Config);
        var model = ServerViewModel.FromServer(service.GetServer("srv01")!);

        var controller = new ServersController(service, new Mock<ILogger<ServersController>>().Object)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };
        await controller.Edit("srv01", model);

        controller.ModelState.IsValid.Should().BeTrue();
        controller.TempData["Success"].Should().NotBeNull();

        var reloaded = await WorkerLoadAsync(await File.ReadAllTextAsync(ConfigPath));
        PendingRulesJson(reloaded).Should().Be(before);
        AssertFullConfig(reloaded);
    }

    // ── Missing block / old config.json ──────────────────────────────────────

    [Fact]
    public async Task MissingBlock_LoadsWithoutPendingRules()
    {
        foreach (var config in new[] { await WorkerLoadAsync(ConfigWithoutBlock), WebUiLoad(ConfigWithoutBlock).Config })
        {
            var scan = config.Servers[0].Checks!.FileMonitoring!.LogScans.Should().ContainSingle().Subject;
            scan.PendingRules.Should().BeEmpty();
            scan.Patterns.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task OldConfig_ServersEditSave_DoesNotAddPendingRules()
    {
        var service = WebUiLoad(ConfigWithoutBlock);
        var model = ServerViewModel.FromServer(service.GetServer("srv01")!);

        await new ServersController(service, new Mock<ILogger<ServersController>>().Object)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        }.Edit("srv01", model);

        var saved = await File.ReadAllTextAsync(ConfigPath);
        (await WorkerLoadAsync(saved)).Servers[0].Checks!.FileMonitoring!.LogScans[0].PendingRules.Should().BeEmpty();
    }

    // ── View model round trip (WebUI form) ──────────────────────────────────

    [Fact]
    public void ViewModel_RoundTripsPendingRulesThroughTheLogScanRow()
    {
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf muss abgeschlossen sein",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    AbortRegex = "abgebrochen",
                    MaxMinutes = 45,
                    Severity = "Warning",
                    ReportAbandoned = false,
                    TimestampRegex = @"\d{4}",
                    TimestampFormat = "yyyy",
                    WikiUrl = "https://wiki.intern/lauf",
                    RemediationSteps = { "Job prüfen" }
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        row.PendingRules.Should().NotBeNullOrEmpty();

        var back = row.ToLogScanConfig();
        back.PendingRules.Should().ContainSingle();
        var rule = back.PendingRules[0];
        rule.Name.Should().Be("Lauf muss abgeschlossen sein");
        rule.StartRegex.Should().Be("waiting for Result");
        rule.EndRegex.Should().Be("Verarbeitungsergebnis:");
        rule.AbortRegex.Should().Be("abgebrochen");
        rule.MaxMinutes.Should().Be(45);
        rule.Severity.Should().Be("Warning");
        rule.ReportAbandoned.Should().BeFalse();
        rule.TimestampRegex.Should().Be(@"\d{4}");
        rule.TimestampFormat.Should().Be("yyyy");
        rule.WikiUrl.Should().Be("https://wiki.intern/lauf");
        rule.RemediationSteps.Should().Equal("Job prüfen");
    }

    // ── M9: der Trenner "::" in einem Wert übersteht den Roundtrip ─────────

    [Fact]
    public void ViewModel_RoundTripsARuleWhoseValueContainsTheSeparator()
    {
        // The regular expression contains the separator "::" — without the escaping it would be
        // split and the expression would be destroyed ("waiting\s+" instead of
        // "waiting\s+::\s+Result").
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = @"waiting\s+::\s+Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    MaxMinutes = 30,
                    Severity = "Critical"
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        var back = row.ToLogScanConfig();

        back.PendingRules.Should().ContainSingle();
        back.PendingRules[0].StartRegex.Should().Be(@"waiting\s+::\s+Result");
    }

    [Fact]
    public void ViewModel_RoundTripsExpectedRunsWithTheSeparator()
    {
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            ExpectedRuns =
            {
                new ExpectedRunRule
                {
                    Name = "Abendlauf",
                    MatchRegex = @"Ergebnis::(OK|Fehler)",
                    Schedule = "Mon-Fri",
                    ExpectedTime = "22:00",
                    GraceMinutes = 120,
                    Severity = "Critical",
                    Holidays = { "2026-12-24", "2026-12-31" },
                    RemediationSteps = { "Schritt 1", "Schritt 2" }
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        var back = row.ToLogScanConfig();

        back.ExpectedRuns.Should().ContainSingle();
        var rule = back.ExpectedRuns[0];
        rule.MatchRegex.Should().Be(@"Ergebnis::(OK|Fehler)");
        rule.Schedule.Should().Be("Mon-Fri");
        rule.ExpectedTime.Should().Be("22:00");
        rule.GraceMinutes.Should().Be(120);
        rule.Holidays.Should().Equal("2026-12-24", "2026-12-31");
        rule.RemediationSteps.Should().Equal("Schritt 1", "Schritt 2");
    }

    // ── M8: die ExtensionData überlebt das Duplizieren eines Scans ──────────

    [Fact]
    public void ViewModel_KeepsTheUnknownFields_WhenTheScanListGrows()
    {
        // The generic merge of the configuration service matches the scans by position. A changed
        // list length (a duplicated scan) would therefore drop the unknown fields — the view model
        // carries them through the form instead.
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log"
        };
        config.ExtensionData["futureScanField"] = System.Text.Json.JsonDocument
            .Parse("\"未来\"").RootElement.Clone();

        var rule = new LogPendingRule
        {
            Name = "Lauf",
            StartRegex = "waiting for Result",
            EndRegex = "Verarbeitungsergebnis:",
            MaxMinutes = 30,
            Severity = "Critical"
        };
        rule.ExtensionData["futureRuleField"] = System.Text.Json.JsonDocument
            .Parse("42").RootElement.Clone();
        config.PendingRules.Add(rule);

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        row.UnknownFieldsJson.Should().NotBeNullOrEmpty();

        // The scan is duplicated: the list is now longer than the original.
        var checks = new FileMonitoringChecksViewModel { Enabled = true };
        checks.LogScans.Add(row);
        checks.LogScans.Add(LogScanRowViewModel.FromLogScanConfig(config));

        var back = checks.ToServerFileMonitoringConfig();

        back.Should().NotBeNull();
        back!.LogScans.Should().HaveCount(2);
        back.LogScans[0].ExtensionData.Should().ContainKey("futureScanField");
        back.LogScans[0].PendingRules.Should().ContainSingle();
        back.LogScans[0].PendingRules[0].ExtensionData.Should().ContainKey("futureRuleField");
        back.LogScans[1].ExtensionData.Should().ContainKey("futureScanField");
    }
}
