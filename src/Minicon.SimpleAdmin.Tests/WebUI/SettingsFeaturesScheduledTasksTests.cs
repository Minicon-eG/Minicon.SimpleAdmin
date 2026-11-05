using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Moq;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Controllers;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.Tests.WebUI;

/// <summary>
/// Settings → Feature-Toggles POST: a save must keep the scheduled task values that are not plain form
/// fields (result codes text, check interval, rule author, UTC expiry) and must not touch other features.
/// </summary>
public sealed class SettingsFeaturesScheduledTasksTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-settings-st-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _expiry = new(2027, 3, 1, 14, 30, 0, DateTimeKind.Utc);

    public SettingsFeaturesScheduledTasksTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private ConfigurationService LoadConfig()
    {
        var config = new Config
        {
            Features = new FeaturesConfig
            {
                EventLog = new EventLogFeatureConfig
                {
                    Enabled = true,
                    IgnoreRules = { new EventLogIgnoreRule { Source = "DCOM", EventIds = { 10016 }, Reason = "Rauschen" } }
                },
                Notifications = new NotificationsFeatureConfig { Enabled = true, AcknowledgesUrl = "http://override/ack.json" },
                ScheduledTasks = new ScheduledTaskFeatureConfig
                {
                    Enabled = true,
                    CheckIntervalSeconds = 300,
                    Defaults = new ScheduledTaskDefaults { MaxRuntimeMinutes = 90, AllowedResultCodes = { 1, 0x41306 } },
                    IgnoreRules =
                    {
                        new ScheduledTaskIgnoreRule
                        {
                            TaskPath = @"\Minicon\Export", EventIds = { 101 }, Servers = { "srv01" },
                            Reason = "bekannt", CreatedBy = @"DOM\alice", ExpiresAt = _expiry
                        }
                    }
                }
            }
        };
        var json = JsonSerializer.Serialize(config, Json);
        File.WriteAllText(ConfigPath, json);
        var service = new ConfigurationService(new ConfigurationBuilder().Build());
        service.LoadFromFile(json, ConfigPath);
        return service;
    }

    /// <summary>Builds the view model the way the form post binds it: only the fields the form renders.</summary>
    private FeaturesViewModel FormPost(ConfigurationService service, Action<FeaturesViewModel>? edit = null)
    {
        var features = JsonSerializer.Deserialize<FeaturesConfig>(JsonSerializer.Serialize(service.GetFeatures(), Json), Json)!;

        // Not rendered as form fields:
        features.ScheduledTasks.CheckIntervalSeconds = null;
        features.ScheduledTasks.Defaults.AllowedResultCodes = new List<int>();
        features.Notifications.AcknowledgesUrl = string.Empty;

        var model = new FeaturesViewModel
        {
            Features = features,
            ScheduledTaskAllowedResultCodes = new ScheduledTaskWatchConfig
            {
                AllowedResultCodes = service.GetFeatures().ScheduledTasks.Defaults.AllowedResultCodes
            }.AllowedResultCodesText
        };
        edit?.Invoke(model);
        return model;
    }

    private static SettingsController Controller(ConfigurationService service) => new(service)
    {
        TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
    };

    private static Config Saved(string path) => JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json)!;

    [Fact]
    public async Task Save_KeepsCodesIntervalAuthorAndUtcExpiry_AndOtherFeatures()
    {
        var service = LoadConfig();

        var result = await Controller(service).Features(FormPost(service));

        result.Should().BeOfType<RedirectToActionResult>();
        var saved = Saved(ConfigPath).Features!;
        saved.ScheduledTasks.Defaults.AllowedResultCodes.Should().Equal(1, 0x41306);
        saved.ScheduledTasks.Defaults.MaxRuntimeMinutes.Should().Be(90);
        saved.ScheduledTasks.CheckIntervalSeconds.Should().Be(300);
        var rule = saved.ScheduledTasks.IgnoreRules.Should().ContainSingle().Subject;
        rule.CreatedBy.Should().Be(@"DOM\alice");
        rule.ExpiresAt!.Value.ToUniversalTime().Should().Be(_expiry);
        rule.Servers.Should().Equal("srv01");

        saved.EventLog.IgnoreRules.Should().ContainSingle().Which.Source.Should().Be("DCOM");
        saved.Notifications.Enabled.Should().BeTrue();
        saved.Notifications.AcknowledgesUrl.Should().Be("http://override/ack.json");
    }

    [Fact]
    public async Task Save_DropsEmptyRuleRows_AndParsesEditedCodes()
    {
        var service = LoadConfig();
        var model = FormPost(service, m =>
        {
            m.ScheduledTaskAllowedResultCodes = "2; 0x800710E0";
            m.Features.ScheduledTasks.IgnoreRules.Add(new ScheduledTaskIgnoreRule { Reason = "leer", ServersText = "srv02" });
            m.Features.ScheduledTasks.IgnoreRules.Add(new ScheduledTaskIgnoreRule { MessageContains = "Zeitüberschreitung", Reason = "neu" });
        });

        await Controller(service).Features(model);

        var saved = Saved(ConfigPath).Features!.ScheduledTasks;
        saved.Defaults.AllowedResultCodes.Should().Equal(2, unchecked((int)0x800710E0));
        saved.IgnoreRules.Select(r => r.Reason).Should().Equal("bekannt", "neu");
    }

    [Fact]
    public async Task Save_ExpiryPostedAsLocalTime_IsStoredAsUtc()
    {
        var service = LoadConfig();
        var local = new DateTime(2027, 6, 1, 8, 0, 0, DateTimeKind.Local);
        var model = FormPost(service, m => m.Features.ScheduledTasks.IgnoreRules[0].ExpiresAt = local);

        await Controller(service).Features(model);

        service.GetFeatures().ScheduledTasks.IgnoreRules[0].ExpiresAt.Should().Be(local.ToUniversalTime());
        service.GetFeatures().ScheduledTasks.IgnoreRules[0].ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }
}
