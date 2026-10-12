using FluentAssertions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.Tests.Services;

public class PowerShellScriptParserTests
{
    // Modelled on the real CopyFilesFromArchiv.ps1 (comment-based help + param block).
    private const string CopyScript = """
        <#
        .SYNOPSIS
            Kopiert Dateien aus Archivverzeichnissen verschiedener Behörden zurück in die Eingangverzeichnisse.

        .DESCRIPTION
            Dieses Script findet automatisch alle Behördenverzeichnisse (R????S Pattern) und kopiert
            Dateien aus dem spezifizierten Archiv-Datum zurück ins Eingangsverzeichnis.

        .PARAMETER SourceDate
            Datum des Quellverzeichnisses im Format YYYY-MM-DD (z.B. "2025-07-20")

        .PARAMETER Application
            Name der Applikation (z.B. "BZR", "ZSTv", "GZR", etc.)

        .PARAMETER TimeFrom
            Startzeit für Filterung im Format HH:mm (z.B. "08:00")

        .PARAMETER TimeTo
            Endzeit für Filterung im Format HH:mm (z.B. "17:00")

        .PARAMETER IncludeAuthorities
            Array von spezifischen Behördencodes die verarbeitet werden sollen (z.B. @("R3100S", "R1234S"))

        .PARAMETER ExcludeAuthorities
            Array von Behördencodes die ausgeschlossen werden sollen

        .PARAMETER LogPath
            Pfad zur Log-CSV-Datei (Standard: C:\Temp\FileCopy_Log.csv)

        .PARAMETER Resume
            Wiederaufnahme-Modus: Überspringe bereits erfolgreich kopierte Dateien

        .PARAMETER WaitBetweenAuthoritiesSeconds
            Wartezeit in Sekunden zwischen verschiedenen Behörden (Standard: 5)

        .PARAMETER DryRun
            Simulationsmodus: Zeigt nur an, was kopiert werden würde

        .EXAMPLE
            .\CopyFilesFromArchiv.ps1 -SourceDate "2025-07-20" -Application "BZR" -DryRun
            Zeigt nur an, was kopiert werden würde
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $false)]
            [string]$SourceDate,

            [Parameter(Mandatory = $false)]
            [string]$Application,

            [string]$TimeFrom,
            [string]$TimeTo,

            [string[]]$IncludeAuthorities = @(),
            [string[]]$ExcludeAuthorities = @("R9999S", 'R0000S'),   # Testbehörden

            [string]$LogPath = "C:\Temp\FileCopy_Log.csv",

            [switch]$Resume,

            [ValidateRange(0, 600)]
            [int]$WaitBetweenAuthoritiesSeconds = 5,

            [switch]$DryRun
        )

        function Show-Menu { param([string]$Title) Write-Host $Title }
        if ($PSBoundParameters.Count -eq 0) { $SourceDate = Read-Host "Datum" }
        """;

    private static ScriptParameterDefinition Param(ScriptDefinition d, string name) =>
        d.Parameters.Single(p => p.Name == name);

    [Fact]
    public void CopyScript_DetectsAllParametersAndControls()
    {
        var d = PowerShellScriptParser.CreateDefinition(@"D:\Skripte\CopyFilesFromArchiv.ps1", CopyScript);

        d.Name.Should().Be("CopyFilesFromArchiv");
        d.Description.Should().StartWith("Kopiert Dateien aus Archivverzeichnissen");
        d.Parameters.Select(p => p.Name).Should().Equal(
            "SourceDate", "Application", "TimeFrom", "TimeTo", "IncludeAuthorities", "ExcludeAuthorities",
            "LogPath", "Resume", "WaitBetweenAuthoritiesSeconds", "DryRun");

        Param(d, "SourceDate").Type.Should().Be(ScriptParameterType.Date);
        Param(d, "SourceDate").EffectiveFormat.Should().Be("yyyy-MM-dd");
        Param(d, "SourceDate").Description.Should().Contain("YYYY-MM-DD");
        Param(d, "Application").Type.Should().Be(ScriptParameterType.Text);
        Param(d, "TimeFrom").Type.Should().Be(ScriptParameterType.Time);
        Param(d, "TimeTo").Type.Should().Be(ScriptParameterType.Time);
        Param(d, "IncludeAuthorities").Type.Should().Be(ScriptParameterType.List);
        Param(d, "IncludeAuthorities").DefaultValue.Should().Be("");
        Param(d, "ExcludeAuthorities").DefaultValue.Should().Be("R9999S\nR0000S");
        Param(d, "LogPath").Type.Should().Be(ScriptParameterType.File);
        Param(d, "LogPath").DefaultValue.Should().Be(@"C:\Temp\FileCopy_Log.csv");
        Param(d, "Resume").Type.Should().Be(ScriptParameterType.Switch);
        Param(d, "DryRun").Type.Should().Be(ScriptParameterType.Switch);
        Param(d, "DryRun").DefaultValue.Should().BeNull();

        var wait = Param(d, "WaitBetweenAuthoritiesSeconds");
        wait.Type.Should().Be(ScriptParameterType.Number);
        wait.DefaultValue.Should().Be("5");
        wait.Min.Should().Be(0);
        wait.Max.Should().Be(600);
        wait.PsType.Should().Be("int");
    }

    [Fact]
    public void ValidateSet_BecomesDropdown_ArrayBecomesMultiSelect()
    {
        const string script = """
            param(
                [Parameter(Mandatory)][ValidateSet('BZR', 'ZSTv', "GZR")][string]$Application,
                [ValidateSet('A','B', IgnoreCase = $false)][string[]]$Modes
            )
            """;

        var d = PowerShellScriptParser.CreateDefinition("x.ps1", script);

        Param(d, "Application").Type.Should().Be(ScriptParameterType.Choice);
        Param(d, "Application").Choices.Should().Equal("BZR", "ZSTv", "GZR");
        Param(d, "Application").Required.Should().BeTrue();
        Param(d, "Application").AllowMultiple.Should().BeFalse();
        Param(d, "Modes").Type.Should().Be(ScriptParameterType.Choice);
        Param(d, "Modes").Choices.Should().Equal("A", "B");
        Param(d, "Modes").AllowMultiple.Should().BeTrue();
    }

    [Theory]
    [InlineData("[datetime]$Stichtag", ScriptParameterType.Date)]
    [InlineData("[datetime]$StartTime", ScriptParameterType.DateTime)]
    [InlineData("[System.IO.FileInfo]$Input", ScriptParameterType.File)]
    [InlineData("[System.IO.DirectoryInfo]$Target", ScriptParameterType.Folder)]
    [InlineData("[string]$TargetFolder", ScriptParameterType.Folder)]
    [InlineData("[string]$ArchiveDirectory", ScriptParameterType.Folder)]
    [InlineData("[string]$OutputFile", ScriptParameterType.File)]
    [InlineData("[string]$Timeout", ScriptParameterType.Text)]
    [InlineData("[double]$Factor", ScriptParameterType.Number)]
    [InlineData("[bool]$Enabled", ScriptParameterType.Switch)]
    [InlineData("$Untyped", ScriptParameterType.Text)]
    public void InfersControlFromTypeAndName(string declaration, ScriptParameterType expected)
    {
        var d = PowerShellScriptParser.CreateDefinition("x.ps1", $"param({declaration})");
        d.Parameters.Single().Type.Should().Be(expected);
    }

    [Fact]
    public void PathDocumentedAsDirectory_BecomesFolderChooser()
    {
        const string script = """
            <#
            .PARAMETER SourcePath
                Verzeichnis, aus dem kopiert wird
            #>
            param([string]$SourcePath)
            """;
        PowerShellScriptParser.CreateDefinition("x.ps1", script).Parameters.Single().Type.Should().Be(ScriptParameterType.Folder);
    }

    [Fact]
    public void GermanDateFormatInHelp_IsUsedAsTransferFormat()
    {
        const string script = """
            <#
            .PARAMETER Stichtag
                Datum im Format DD.MM.YYYY
            #>
            param([string]$Stichtag)
            """;
        var p = PowerShellScriptParser.CreateDefinition("x.ps1", script).Parameters.Single();
        p.Type.Should().Be(ScriptParameterType.Date);
        p.EffectiveFormat.Should().Be("dd.MM.yyyy");
    }

    [Fact]
    public void HelpMessage_ValidatePattern_AndExpressionDefaults()
    {
        const string script = """
            param(
                [Parameter(Mandatory=$true, HelpMessage = "Behördencode, z.B. R3100S")]
                [ValidatePattern('^R\d{4}S$')]
                [string]$Authority,
                [string]$Day = (Get-Date).ToString('yyyy-MM-dd'),
                [string]$Out = "$env:TEMP\out.csv",
                [string]$Quote = 'it''s'
            )
            """;
        var d = PowerShellScriptParser.CreateDefinition("x.ps1", script);

        Param(d, "Authority").Required.Should().BeTrue();
        Param(d, "Authority").Description.Should().Be("Behördencode, z.B. R3100S");
        Param(d, "Authority").Pattern.Should().Be(@"^R\d{4}S$");
        Param(d, "Day").DefaultValue.Should().BeNull();
        Param(d, "Day").DefaultExpression.Should().Be("(Get-Date).ToString('yyyy-MM-dd')");
        Param(d, "Out").DefaultValue.Should().BeNull();
        Param(d, "Out").DefaultExpression.Should().Be("\"$env:TEMP\\out.csv\"");
        Param(d, "Quote").DefaultValue.Should().Be("it's");
    }

    [Fact]
    public void IgnoresParamBlocksOfFunctions_AndParamInStringsOrComments()
    {
        const string script = """
            # param($Fake)
            $text = "param(`$AlsoFake)"
            function Foo { param([string]$Inner) }
            """;
        var parsed = PowerShellScriptParser.Parse(script);
        parsed.HasParamBlock.Should().BeFalse();
        parsed.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Merge_KeepsAdminAdjustments_AddsNew_DropsRemoved()
    {
        var existing = new List<ScriptParameterDefinition>
        {
            new() { Name = "SourceDate", Label = "Archivdatum", Type = ScriptParameterType.Date, Fixed = false, DefaultValue = "2025-01-01" },
            new() { Name = "LogPath", Type = ScriptParameterType.Text, Fixed = true, DefaultValue = @"D:\Logs\copy.csv" },
            new() { Name = "Removed" }
        };
        var fresh = PowerShellScriptParser.CreateDefinition("x.ps1", """
            param([Parameter(Mandatory)][string]$SourceDate, [string]$LogPath = "C:\Temp\x.csv", [switch]$DryRun)
            """).Parameters;

        var merged = PowerShellScriptParser.MergeParameters(existing, fresh);

        merged.Select(p => p.Name).Should().Equal("SourceDate", "LogPath", "DryRun");
        merged[0].Label.Should().Be("Archivdatum");
        merged[0].Required.Should().BeTrue("mandatory comes from the script");
        merged[0].DefaultValue.Should().Be("2025-01-01");
        merged[1].Type.Should().Be(ScriptParameterType.Text, "admin override of the control type is kept");
        merged[1].Fixed.Should().BeTrue();
        merged[1].DefaultValue.Should().Be(@"D:\Logs\copy.csv");
        merged[2].Type.Should().Be(ScriptParameterType.Switch);
    }

    [Theory]
    [InlineData("WaitBetweenFilesSeconds", "Wait Between Files Seconds")]
    [InlineData("SourceDate", "Source Date")]
    [InlineData("DryRun", "Dry Run")]
    [InlineData("IPAddress", "IP Address")]
    public void HumanizeName_SplitsCamelCase(string name, string expected) =>
        ScriptParameterDefinition.HumanizeName(name).Should().Be(expected);
}
