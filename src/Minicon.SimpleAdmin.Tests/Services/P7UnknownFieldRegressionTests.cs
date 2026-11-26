using System.Text.Json;
using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.Tests.Services;

public sealed class P7UnknownFieldRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreserveUnknownFields_SurvivesFileMonitoringRename(bool logScan)
    {
        var original = new ServerFileMonitoringConfig();
        var replacement = new ServerFileMonitoringConfig();
        ConfigModel renamed;
        if (logScan)
        {
            original.LogScans.Add(new LogScanConfig { Name = "Alt", Path = "/log/a.log" });
            replacement.LogScans.Add(new LogScanConfig { Name = "Neu", Path = "/log/a.log" });
            original.LogScans[0].ExtensionData["futureField"] = Value(42);
            renamed = replacement.LogScans[0];
        }
        else
        {
            original.Directories.Add(new WatchedDirectory { Name = "Alt", Path = "/data" });
            replacement.Directories.Add(new WatchedDirectory { Name = "Neu", Path = "/data" });
            original.Directories[0].ExtensionData["futureField"] = Value(42);
            renamed = replacement.Directories[0];
        }

        ConfigurationService.PreserveUnknownFields(replacement, original);

        renamed.ExtensionData.Should().ContainKey("futureField").WhoseValue.GetInt32().Should().Be(42);
    }

    [Fact]
    public void RuleRename_PreservesUnknownFields()
    {
        var scan = new LogScanConfig
        {
            Name = "Scan",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Alt",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:"
                }
            }
        };
        scan.PendingRules[0].ExtensionData["futureRuleField"] = Value(43);
        var row = LogScanRowViewModel.FromLogScanConfig(scan);
        row.PendingRules = row.PendingRules!.Replace("name=Alt", "name=Neu", StringComparison.Ordinal);

        var renamed = row.ToLogScanConfig().PendingRules[0];

        renamed.Name.Should().Be("Neu");
        renamed.ExtensionData.Should().ContainKey("futureRuleField").WhoseValue.GetInt32().Should().Be(43);
    }

    private static JsonElement Value(int value) => JsonDocument.Parse(value.ToString()).RootElement.Clone();
}
