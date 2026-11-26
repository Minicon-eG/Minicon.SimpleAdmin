using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Regression tests for tail reading, visible evaluation errors, preview behavior,
/// unknown-field preservation, escaped rule values and capped running-run findings.
/// </summary>
public sealed class FileMonitoringNachbesserungRunde2Tests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "simpleadmin-tests", "p7-runde2-" + Guid.NewGuid().ToString("N"));

    public FileMonitoringNachbesserungRunde2Tests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* temp */ }
    }

    // 2026-11-25 ist ein Mittwoch, 2026-11-26 ein Donnerstag.
    private static readonly DateTime WednesdayNoon = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Local);

    private static FileShareChecker Build(IFileSystemAccess fs) => new(fs, NullLogger<FileShareChecker>.Instance);

    private static LogPendingRule PendingRule(int maxMinutes = 30) => new()
    {
        Name = "Lauf muss abgeschlossen sein",
        StartRegex = "waiting for Result",
        EndRegex = "Verarbeitungsergebnis:",
        MaxMinutes = maxMinutes,
        Severity = "Critical"
    };

    // ═══════════════════════════════════════════════════════════════════════
    // N7 — der Tail einer Datei wird je Scan genau einmal gelesen.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void N7_EachFileIsReadExactlyOnce_PerScan()
    {
        // Ein Scan mit Signatur, Pending-Regel und Expected-Rule las dieselbe Datei
        // dreimal (5.000 / 20.000 / 20.000 Zeilen). Der Tail wird jetzt einmal je Datei
        // gelesen und gemeinsam ausgewertet.
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/kasse.log"))
            .Returns(new[] { new FileEntry("/log/kasse.log", "kasse.log", WednesdayNoon) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { "2026-11-25 22:05:00.000 Verarbeitungsergebnis: OK" });

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Kassen-Log",
                    Path = "/log/kasse.log",
                    MaxLines = 5000,
                    Patterns = { new LogPatternConfig { Name = "Hängend", Severity = "Critical", Regex = "Verarbeitungsergebnis:" } },
                    PendingRules = { PendingRule(30) },
                    ExpectedRuns =
                    {
                        new ExpectedRunRule
                        {
                            Name = "Abendlauf",
                            MatchRegex = "Verarbeitungsergebnis:",
                            Schedule = "Mon-Fri",
                            ExpectedTime = "22:00",
                            GraceMinutes = 120
                        }
                    }
                }
            }
        };

        Build(fs.Object).Check(config, new FileMonitoringDefaults(), WednesdayNoon);

        fs.Verify(f => f.ReadLines("/log/kasse.log", It.IsAny<int>()), Times.Once(),
            "der Tail der Datei wird einmal gelesen und für alle Regelarten verwendet");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N8 — der Tail ist identisch zu File.ReadAllLines(...).TakeLast(n).
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("a\nb\nc\nd\n", 2)]
    [InlineData("a\nb\nc\nd\n", 5)]
    [InlineData("a\nb\nc\nd\n", 10)]
    [InlineData("a\n\n", 1)]
    [InlineData("a\n\n", 2)]
    [InlineData("\nA\nB", 3)]
    [InlineData("\nA\nB", 2)]
    [InlineData("a\r\nb\r\nc\r\n", 2)]
    [InlineData("a\rb\rc\r", 2)]
    [InlineData("a\rb\rc\r", 3)]
    [InlineData("only-one-line", 5)]
    [InlineData("", 5)]
    public void N8_ReadTail_IsIdenticalToReadAllLinesTakeLast(string content, int maxLines)
    {
        var path = Path.Combine(_tempDirectory, $"tail-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var expected = File.ReadAllLines(path).TakeLast(maxLines).ToArray();
        var actual = new FileSystemAccess().ReadLines(path, maxLines).ToArray();

        actual.Should().Equal(expected,
            $"der Tail muss identisch zu File.ReadAllLines(...).TakeLast({maxLines}) sein");
    }

    [Fact]
    public void N8_ReadTail_HandlesAUtf8Bom()
    {
        var path = Path.Combine(_tempDirectory, $"bom-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "first\nsecond\n", new UTF8Encoding(true));

        var expected = File.ReadAllLines(path).TakeLast(10).ToArray();
        var actual = new FileSystemAccess().ReadLines(path, 10).ToArray();

        actual.Should().Equal(expected, "die BOM gehört nicht zur ersten Zeile");
    }

    [Fact]
    public void N8_ReadTail_HandlesAUtf16Bom()
    {
        var path = Path.Combine(_tempDirectory, $"utf16-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "first\nsecond\n", Encoding.Unicode);

        var expected = File.ReadAllLines(path).TakeLast(10).ToArray();
        var actual = new FileSystemAccess().ReadLines(path, 10).ToArray();

        actual.Should().Equal(expected, "eine UTF-16-Datei wird ebenfalls korrekt gelesen");
    }

    [Fact]
    public void N8_ReadTail_KeepsUtf8CharactersAcrossTheBlockBoundary()
    {
        // Der Puffer umfasst mehrere 64-KB-Blöcke — ein mehrbyte-UTF-8-Zeichen darf an der
        // Blockgrenze nicht zerschnitten werden.
        var path = Path.Combine(_tempDirectory, $"utf8-{Guid.NewGuid():N}.log");
        var builder = new StringBuilder();
        for (var i = 0; i < 5000; i++)
        {
            builder.Append("2026-11-25 10:00:00.000 Zeile mit Umlauten: äöüß ").Append(i).Append('\n');
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));

        var expected = File.ReadAllLines(path).TakeLast(500).ToArray();
        var actual = new FileSystemAccess().ReadLines(path, 500).ToArray();

        actual.Should().Equal(expected);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N9 — Fehler erscheinen nie als „OK“.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rendert die statische Seite für einen Zustand und liefert das HTML zurück.
    /// Der Test prüft die <em>sichtbare</em> Ausgabe, damit er gegen den Stand vor der
    /// Nachbesserung (der den internen Zähler noch nicht hatte) aussagekräftig bleibt.
    /// </summary>
    private async Task<string> RenderBadgeAsync(FileMonitoringState fileMonitoring)
    {
        var generator = new Minicon.SimpleAdmin.HtmlGenerator.StaticHtmlGenerator(
            _tempDirectory, NullLogger<Minicon.SimpleAdmin.HtmlGenerator.StaticHtmlGenerator>.Instance);

        var statuses = new List<Minicon.SimpleAdmin.Models.Status.RuntimeStatus>
        {
            new()
            {
                Server = "server-a",
                Service = "api",
                LastUpdate = DateTime.UtcNow,
                Status = Minicon.SimpleAdmin.Models.Status.ServiceStatus.Unhealthy,
                FileMonitoring = fileMonitoring
            }
        };

        await generator.GenerateAllAsync(statuses, new Config(), "server-a", new List<Acknowledge>());

        return await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "index.html"));
    }

    [Fact]
    public async Task N9_AnUnreachablePath_IsNeverShownAsOk()
    {
        // Ein Zugriffsfehler erzeugte eine rote Badge mit dem Text „📁 OK Dateien“ — der
        // Fehler war sichtbar, der Text behauptete trotzdem das Gegenteil.
        var html = await RenderBadgeAsync(new FileMonitoringState
        {
            AnyError = true,
            Error = "Freigabe nicht erreichbar"
        });

        html.Should().NotContain("📁 OK Dateien",
            "ein Zugriffsfehler darf nie als „OK“ erscheinen");
        html.Should().Contain("Fehler");
        html.Should().Contain("badge-critical");
    }

    [Fact]
    public async Task N9_AnEvaluationError_IsNeverShownAsOk()
    {
        // Ein Auswertungsfehler (z. B. ein Regex-Timeout) blieb in der Badge grün/„OK“
        // und wurde in der Detailansicht nicht ausgegeben. Vor der Nachbesserung wurde
        // der Fehler im Zustand überhaupt nicht abgelegt — er verschwand spurlos.
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/kasse.log"))
            .Returns(new[] { new FileEntry("/log/kasse.log", "kasse.log", WednesdayNoon) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { new string('a', 60) + "b" });

        var rule = new ExpectedRunRule
        {
            Name = "Abendlauf",
            MatchRegex = "^(a+)+$",   // echtes Backtracking → Regex-Timeout
            Schedule = "Mon-Fri",
            ExpectedTime = "22:00",
            GraceMinutes = 120,
            Severity = "Critical"
        };

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig
                {
                    Name = "Kassen-Log",
                    Path = "/log/kasse.log",
                    ExpectedRuns = { rule }
                }
            }
        };

        var state = Build(fs.Object).Check(
            config, new FileMonitoringDefaults(), new DateTime(2026, 11, 26, 0, 30, 0, DateTimeKind.Local));

        state.MissingRuns.Should().BeEmpty(
            "aus einer unvollständigen Auswertung darf kein Missing abgeleitet werden");

        var html = await RenderBadgeAsync(state);

        html.Should().NotContain("📁 OK Dateien",
            "ein Auswertungsfehler darf nie als „OK“ erscheinen");
        html.Should().Contain("Fehler");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N10 — der Trockenlauf verarbeitet die Dateien wie der Worker.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task N10_Preview_ShowsAnOpenRunInTheNewestFile()
    {
        // Gegenprobe: ein Lauf, der in der neuesten Datei offen bleibt, wird weiterhin
        // als „offen“ angezeigt.
        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles("/log/app.log*")).Returns(new[]
        {
            new FileEntry("/log/app.log", "app.log", DateTime.Now.AddMinutes(-120))
        });
        fs.Setup(f => f.ReadLines("/log/app.log", It.IsAny<int>())).Returns(new[]
        {
            "waiting for Result..."
        });

        var scan = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/app.log*",
            PendingRules = { PendingRule(30) }
        };

        var preview = await new FileMonitoringPreviewService(fs.Object).PreviewLogScanAsync(scan, null);

        preview.PendingRuns.Should().ContainSingle(
            "der Lauf ist älter als die erlaubten 30 Minuten");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N11 — die unbekannten Felder folgen der Regel, nicht der Position.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void N11_UnknownFieldsOfARule_FollowTheRule_AndNotThePosition()
    {
        // Zwei Regeln mit unterschiedlichen unbekannten Feldern. Nach dem Vertauschen
        // der Regeln darf Regel B nicht die unbekannten Felder von Regel A erben.
        var ruleA = new LogPendingRule
        {
            Name = "Regel A",
            StartRegex = "waiting for Result",
            EndRegex = "Verarbeitungsergebnis:",
            MaxMinutes = 30,
            Severity = "Critical"
        };
        ruleA.ExtensionData["futureFieldA"] = System.Text.Json.JsonDocument.Parse("\"A\"").RootElement.Clone();

        var ruleB = new LogPendingRule
        {
            Name = "Regel B",
            StartRegex = "waiting for Result",
            EndRegex = "Verarbeitungsergebnis:",
            MaxMinutes = 45,
            Severity = "Warning"
        };
        ruleB.ExtensionData["futureFieldB"] = System.Text.Json.JsonDocument.Parse("\"B\"").RootElement.Clone();

        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules = { ruleA, ruleB }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);

        // Die Regeln werden im Formular vertauscht.
        var lines = row.PendingRules!.Split('\n').ToList();
        row.PendingRules = string.Join("\n", new[] { lines[1], lines[0] });

        var back = row.ToLogScanConfig();

        back.PendingRules.Should().HaveCount(2);
        var byName = back.PendingRules.ToDictionary(r => r.Name!);
        byName.Should().ContainKey("Regel A");
        byName.Should().ContainKey("Regel B");

        byName["Regel A"].ExtensionData.Should().ContainKey("futureFieldA");
        byName["Regel A"].ExtensionData.Should().NotContainKey("futureFieldB");
        byName["Regel B"].ExtensionData.Should().ContainKey("futureFieldB");
        byName["Regel B"].ExtensionData.Should().NotContainKey("futureFieldA");
    }

    [Fact]
    public void N11_PreserveUnknownFields_KeepsTheUnknownFieldsOfADirectory_WhenTheListGrows()
    {
        // Der echte Speicherweg (ConfigurationService.PreserveUnknownFields) kannte für
        // überwachte Verzeichnisse keinen stabilen Schlüssel: die Liste wurde rein
        // positionsbasiert zugeordnet. Beim Duplizieren eines Verzeichnisses wuchs die
        // Liste — die Zuordnung entfiel, die unbekannten Felder gingen verloren.
        // Der Schlüssel ist jetzt der Pfad des Verzeichnisses.
        var original = new ServerFileMonitoringConfig
        {
            Enabled = true,
            Directories =
            {
                new WatchedDirectory { Name = "Übergabe", Path = "\\\\srv\\d$\\Daten\\uebergabe" }
            }
        };
        original.Directories[0].ExtensionData["futureDirectoryField"] =
            System.Text.Json.JsonDocument.Parse("42").RootElement.Clone();

        var replacement = new ServerFileMonitoringConfig
        {
            Enabled = true,
            Directories =
            {
                new WatchedDirectory { Name = "Übergabe", Path = "\\\\srv\\d$\\Daten\\uebergabe" },
                new WatchedDirectory { Name = "Übergabe (Kopie)", Path = "\\\\srv\\d$\\Daten\\uebergabe" }
            }
        };

        ConfigurationService.PreserveUnknownFields(replacement, original);

        replacement.Directories[0].ExtensionData.Should().ContainKey("futureDirectoryField",
            "das unbekannte Feld muss dem Verzeichnis folgen, nicht der Listenposition");
        replacement.Directories[1].ExtensionData.Should().NotContainKey("futureDirectoryField",
            "die Kopie ist ein neuer Eintrag — sie darf kein Feld erben");
    }

    [Fact]
    public void N11_PreserveUnknownFields_KeepsTheUnknownFieldsOfALogScan_WhenTheListGrows()
    {
        // Dasselbe gilt für Log-Scans (Schlüssel: Pfad).
        var original = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig { Name = "Schnittstellen-Log", Path = "\\\\srv\\d$\\log\\*.log" }
            }
        };
        original.LogScans[0].ExtensionData["futureScanField"] =
            System.Text.Json.JsonDocument.Parse("\"x\"").RootElement.Clone();

        var replacement = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans =
            {
                new LogScanConfig { Name = "Schnittstellen-Log", Path = "\\\\srv\\d$\\log\\*.log" },
                new LogScanConfig { Name = "Schnittstellen-Log (Kopie)", Path = "\\\\srv\\d$\\log\\*.log" }
            }
        };

        ConfigurationService.PreserveUnknownFields(replacement, original);

        replacement.LogScans[0].ExtensionData.Should().ContainKey("futureScanField",
            "das unbekannte Feld muss dem Log-Scan folgen, nicht der Listenposition");
        replacement.LogScans[1].ExtensionData.Should().NotContainKey("futureScanField",
            "die Kopie ist ein neuer Eintrag — sie darf kein Feld erben");
    }

    [Fact]
    public async Task N11_UnknownFieldsOfADuplicatedDirectory_AreKept()
    {
        // Beim Duplizieren eines überwachten Verzeichnisses gingen dessen unbekannte
        // Felder im echten Konfigurationspfad verloren. Der Test läuft über den echten
        // Speicherpfad des WebUI: config.json → ServerViewModel (Formular) →
        // ToServer(originalChecks) → ConfigurationService.UpdateServer → SaveToFileAsync.
        // Nur dieser Weg entscheidet, ob die unbekannten Felder die Datei überleben.
        const string json = """
            {
              "servers": [
                {
                  "name": "srv01",
                  "active": true,
                  "checks": {
                    "fileMonitoring": {
                      "enabled": true,
                      "directories": [
                        {
                          "name": "Übergabe",
                          "path": "\\\\srv\\d$\\Daten\\uebergabe",
                          "recursive": true,
                          "ageMinutesWarning": 60,
                          "ageMinutesCritical": 120,
                          "futureDirectoryField": 42
                        }
                      ]
                    }
                  }
                }
              ]
            }
            """;

        var configPath = Path.Combine(_tempDirectory, "config.json");
        File.WriteAllText(configPath, json);

        var service = new ConfigurationService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        service.LoadFromFile(json, configPath);

        var server = service.GetServer("srv01");
        server.Should().NotBeNull();
        server!.Checks!.FileMonitoring.Should().NotBeNull();
        server.Checks!.FileMonitoring!.Directories.Should().ContainSingle();

        // Das Formular wird geladen und das Verzeichnis im UI dupliziert
        // (derselbe Weg wie der „Kopieren“-Knopf in der Serverkarte).
        var viewModel = ServerViewModel.FromServer(server);
        viewModel.Checks.Should().NotBeNull();
        var fileMonitoring = viewModel.Checks!.FileMonitoring;
        fileMonitoring.Directories.Should().ContainSingle();

        var copy = WatchedDirectoryRowViewModel.FromWatchedDirectory(
            server.Checks!.FileMonitoring!.Directories[0]);
        copy.Name = "Übergabe (Kopie)";
        fileMonitoring.Directories.Add(copy);

        // Das Formular wird zurückgeschrieben — der echte Speicherpfad.
        var originalChecks = service.GetServer("srv01")?.Checks;
        var updated = viewModel.ToServer(originalChecks);
        service.UpdateServer("srv01", updated);
        await service.SaveToFileAsync();

        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        var directories = saved.RootElement
            .GetProperty("servers")[0]
            .GetProperty("checks")
            .GetProperty("fileMonitoring")
            .GetProperty("directories");

        directories.GetArrayLength().Should().Be(2,
            "das duplizierte Verzeichnis muss gespeichert werden");
        foreach (var directory in directories.EnumerateArray())
        {
            directory.TryGetProperty("futureDirectoryField", out var value).Should().BeTrue(
                $"das unbekannte Feld des Verzeichnisses '{directory.GetProperty("name").GetString()}' " +
                "darf beim Duplizieren nicht verloren gehen");
            value.GetInt32().Should().Be(42);
        }
    }

    [Fact]
    public void N11_UnknownFieldsOfADuplicatedDirectory_AreKept_InTheViewModelPath()
    {
        // Gegenprobe auf ViewModel-Ebene: das Formular muss die unbekannten Felder
        // bereits vor dem Speichern durch das Verzeichnis (nicht durch die Position)
        // transportieren.
        var directory = new WatchedDirectory
        {
            Name = "Übergabe",
            Path = "\\\\srv\\d$\\Daten\\uebergabe",
            Recursive = true,
            AgeMinutesWarning = 60,
            AgeMinutesCritical = 120
        };
        directory.ExtensionData["futureDirectoryField"] =
            System.Text.Json.JsonDocument.Parse("42").RootElement.Clone();

        var row = WatchedDirectoryRowViewModel.FromWatchedDirectory(directory);

        var copy = WatchedDirectoryRowViewModel.FromWatchedDirectory(directory);
        copy.Name = "Übergabe (Kopie)";

        var checks = new FileMonitoringChecksViewModel { Enabled = true };
        checks.Directories.Add(row);
        checks.Directories.Add(copy);

        var back = checks.ToServerFileMonitoringConfig();

        back.Should().NotBeNull();
        back!.Directories.Should().HaveCount(2);
        back.Directories[0].ExtensionData.Should().ContainKey("futureDirectoryField",
            "die unbekannten Felder müssen das Formular durchlaufen");
        back.Directories[1].ExtensionData.Should().ContainKey("futureDirectoryField",
            "auch die Kopie muss die unbekannten Felder behalten");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N12 — der Listentrenner „|“ übersteht den Roundtrip verlustfrei.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void N12_ARemediationStepContainingTheListSeparator_SurvivesTheRoundtrip()
    {
        // Ein Schritt „A|B“ wurde beim Roundtrip in zwei Schritte zerlegt.
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    MaxMinutes = 30,
                    Severity = "Critical",
                    RemediationSteps = { "A|B", "C" }
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        var back = row.ToLogScanConfig();

        back.PendingRules.Should().ContainSingle();
        back.PendingRules[0].RemediationSteps.Should().HaveCount(2,
            "ein Schritt mit „|“ darf nicht in zwei Schritte zerlegt werden");
        back.PendingRules[0].RemediationSteps[0].Should().Be("A|B");
        back.PendingRules[0].RemediationSteps[1].Should().Be("C");
    }

    [Fact]
    public void N12_AnExpectedRuleWithAStepContainingTheListSeparator_SurvivesTheRoundtrip()
    {
        var config = new LogScanConfig
        {
            Name = "Kassen-Log",
            Path = "/log/kasse.log",
            ExpectedRuns =
            {
                new ExpectedRunRule
                {
                    Name = "Abendlauf",
                    MatchRegex = "Verarbeitungsergebnis:",
                    Schedule = "Mon-Fri",
                    ExpectedTime = "22:00",
                    GraceMinutes = 120,
                    Severity = "Critical",
                    RemediationSteps = { "A|B", "C" }
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        var back = row.ToLogScanConfig();

        back.ExpectedRuns.Should().ContainSingle();
        back.ExpectedRuns[0].RemediationSteps.Should().Equal("A|B", "C");
    }

    [Fact]
    public void N12_AStepContainingBothSeparators_SurvivesTheRoundtrip()
    {
        // „A|B“ und „C::D“ in einem Schritt — beide Trenner müssen verlustfrei überleben.
        var config = new LogScanConfig
        {
            Name = "Schnittstellen-Log",
            Path = "/log/a.log",
            PendingRules =
            {
                new LogPendingRule
                {
                    Name = "Lauf",
                    StartRegex = "waiting for Result",
                    EndRegex = "Verarbeitungsergebnis:",
                    MaxMinutes = 30,
                    Severity = "Critical",
                    RemediationSteps = { "A|B::C" }
                }
            }
        };

        var row = LogScanRowViewModel.FromLogScanConfig(config);
        var back = row.ToLogScanConfig();

        back.PendingRules.Should().ContainSingle();
        back.PendingRules[0].RemediationSteps.Should().Equal("A|B::C");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // N13 — die Kappung der laufenden Läufe ist über mehrere Scans nachweisbar.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void N13_TheCapOnRunningRuns_IsEffective_AcrossSeveralScans()
    {
        // Der bisherige Test erzeugte in *einem* Scan 60 Startzeilen — daraus entsteht nur
        // ein einziger Running-Eintrag (eine offene Runde je Scan). Der Test war damit
        // nicht fixempfindlich und bestand auch mit dem alten Code.
        // Sechs *unabhängige* Scans erzeugen sechs Running-Einträge — mehr als die
        // MaxReportedItems (5).
        var scans = new List<LogScanConfig>();
        for (var i = 0; i < 6; i++)
        {
            var path = $"/log/scan-{i}.log";
            scans.Add(new LogScanConfig
            {
                Name = $"Scan {i}",
                Path = path,
                PendingRules = { PendingRule(600) }
            });
        }

        var fs = new Mock<IFileSystemAccess>();
        fs.Setup(f => f.ResolveLogFiles(It.IsAny<string>()))
            .Returns((string path) => new[] { new FileEntry(path, Path.GetFileName(path), WednesdayNoon) });
        fs.Setup(f => f.ReadLines(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new[] { "2026-11-25 11:59:00.000 waiting for Result..." });

        var config = new ServerFileMonitoringConfig
        {
            Enabled = true,
            LogScans = scans
        };

        var state = Build(fs.Object).Check(
            config, new FileMonitoringDefaults { MaxReportedItems = 5 }, WednesdayNoon);

        state.RunningLogRuns.Should().HaveCount(5,
            "sechs unabhängige Scans erzeugen sechs laufende Läufe — sie werden auf 5 gekappt");
    }
}
