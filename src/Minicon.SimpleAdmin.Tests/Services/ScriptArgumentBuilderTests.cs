using System.Text;
using FluentAssertions;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ScriptArgumentBuilderTests
{
    private static ScriptDefinition Def(params ScriptParameterDefinition[] parameters) =>
        new() { Name = "Test", Path = "/tmp/test.ps1", Parameters = parameters.ToList() };

    private static Dictionary<string, List<string>> Input(params (string Name, string[] Values)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Values.ToList());

    private static ScriptArgument Single(ScriptDefinition d, Dictionary<string, List<string>> input)
    {
        var (args, errors) = ScriptArgumentBuilder.Build(d, input);
        errors.Should().BeEmpty();
        return args.Should().ContainSingle().Subject;
    }

    [Fact]
    public void Date_FromHtmlInput_IsPassedInConfiguredFormat()
    {
        var d = Def(new ScriptParameterDefinition { Name = "SourceDate", Type = ScriptParameterType.Date });
        Single(d, Input(("SourceDate", new[] { "2025-07-20" }))).Values.Should().Equal("2025-07-20");

        d.Parameters[0].Format = "dd.MM.yyyy";
        Single(d, Input(("SourceDate", new[] { "2025-07-20" }))).Values.Should().Equal("20.07.2025");
    }

    [Fact]
    public void Time_IsNormalized()
    {
        var d = Def(new ScriptParameterDefinition { Name = "TimeFrom", Type = ScriptParameterType.Time });
        Single(d, Input(("TimeFrom", new[] { "8:00" }))).Values.Should().Equal("08:00");
    }

    [Fact]
    public void InvalidDate_IsRejected()
    {
        var d = Def(new ScriptParameterDefinition { Name = "SourceDate", Type = ScriptParameterType.Date });
        var (_, errors) = ScriptArgumentBuilder.Build(d, Input(("SourceDate", new[] { "gestern" })));
        errors.Should().ContainSingle().Which.Should().Contain("kein gültiges Datum");
    }

    [Fact]
    public void Required_EmptyValue_IsRejected_OptionalEmpty_IsOmitted()
    {
        var d = Def(
            new ScriptParameterDefinition { Name = "Application", Required = true },
            new ScriptParameterDefinition { Name = "TimeTo", Type = ScriptParameterType.Time });
        var (args, errors) = ScriptArgumentBuilder.Build(d, Input(("Application", new[] { "  " }), ("TimeTo", new[] { "" })));
        args.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Contain("Pflichtfeld");
    }

    [Fact]
    public void Switch_OnlyPassedWhenOn()
    {
        var d = Def(
            new ScriptParameterDefinition { Name = "DryRun", Type = ScriptParameterType.Switch },
            new ScriptParameterDefinition { Name = "Force", Type = ScriptParameterType.Switch });
        var (args, errors) = ScriptArgumentBuilder.Build(d, Input(("DryRun", new[] { "true" }), ("Force", new[] { "false" })));
        errors.Should().BeEmpty();
        var arg = args.Should().ContainSingle().Subject;
        arg.Name.Should().Be("DryRun");
        arg.Kind.Should().Be('b');
        arg.Values.Should().Equal("1");
    }

    [Fact]
    public void List_SplitsLinesAndCommas()
    {
        var d = Def(new ScriptParameterDefinition { Name = "IncludeAuthorities", Type = ScriptParameterType.List, Pattern = @"^R\d{4}S$" });
        var arg = Single(d, Input(("IncludeAuthorities", new[] { "R3100S, R1234S\nR0001S" })));
        arg.Kind.Should().Be('a');
        arg.Values.Should().Equal("R3100S", "R1234S", "R0001S");

        var (_, errors) = ScriptArgumentBuilder.Build(d, Input(("IncludeAuthorities", new[] { "R3100S, X" })));
        errors.Should().ContainSingle().Which.Should().Contain("Muster");
    }

    [Fact]
    public void Choice_MustBeAllowed_AndIsNormalizedToDefinedSpelling()
    {
        var d = Def(new ScriptParameterDefinition { Name = "Application", Type = ScriptParameterType.Choice, Choices = { "BZR", "ZSTv" } });
        Single(d, Input(("Application", new[] { "zstv" }))).Values.Should().Equal("ZSTv");

        var (_, errors) = ScriptArgumentBuilder.Build(d, Input(("Application", new[] { "XYZ" })));
        errors.Should().ContainSingle().Which.Should().Contain("kein zulässiger Wert");
    }

    [Fact]
    public void Number_RangeAndIntegerChecks()
    {
        var d = Def(new ScriptParameterDefinition { Name = "Wait", Type = ScriptParameterType.Number, PsType = "int", Min = 0, Max = 60 });
        var arg = Single(d, Input(("Wait", new[] { "10" })));
        arg.Kind.Should().Be('i');
        arg.Values.Should().Equal("10");

        ScriptArgumentBuilder.Build(d, Input(("Wait", new[] { "61" }))).Errors.Should().ContainSingle().Which.Should().Contain("≤ 60");
        ScriptArgumentBuilder.Build(d, Input(("Wait", new[] { "1,5" }))).Errors.Should().ContainSingle().Which.Should().Contain("ganze Zahl");
    }

    [Fact]
    public void Fixed_UsesDefaultAndIgnoresInput_HiddenIsNeverPassed()
    {
        var d = Def(
            new ScriptParameterDefinition { Name = "LogPath", Type = ScriptParameterType.File, Fixed = true, DefaultValue = @"D:\Logs\copy.csv" },
            new ScriptParameterDefinition { Name = "Secret", Hidden = true });
        var (args, errors) = ScriptArgumentBuilder.Build(d, Input(("LogPath", new[] { @"C:\evil.csv" }), ("Secret", new[] { "x" })));
        errors.Should().BeEmpty();
        args.Should().ContainSingle().Which.Values.Should().Equal(@"D:\Logs\copy.csv");
    }

    [Fact]
    public void Encode_IsBase64_SoQuotesAndSemicolonsCannotBreakOut()
    {
        const string hostile = "x'; Remove-Item C:\\ -Recurse; '\"$(whoami)\"\n`t";
        var d = Def(
            new ScriptParameterDefinition { Name = "Application" },
            new ScriptParameterDefinition { Name = "Codes", Type = ScriptParameterType.List },
            new ScriptParameterDefinition { Name = "DryRun", Type = ScriptParameterType.Switch });
        var (args, errors) = ScriptArgumentBuilder.Build(d, Input(
            ("Application", new[] { hostile }), ("Codes", new[] { "Ä1\nB2" }), ("DryRun", new[] { "on" })));
        errors.Should().BeEmpty();

        var encoded = ScriptArgumentBuilder.Encode(args);
        var lines = encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);

        var app = lines[0].Split('\t');
        app[0].Should().Be("Application");
        app[1].Should().Be("s");
        Encoding.UTF8.GetString(Convert.FromBase64String(app[2])).Should().Be(hostile.Trim());

        var codes = lines[1].Split('\t');
        codes[1].Should().Be("a");
        codes[2].Split(',').Select(b => Encoding.UTF8.GetString(Convert.FromBase64String(b))).Should().Equal("Ä1", "B2");

        lines[2].Should().Be("DryRun\tb\t1");
    }

    [Fact]
    public void InvalidParameterName_IsRejected()
    {
        var d = Def(new ScriptParameterDefinition { Name = "x; Remove-Item" });
        ScriptArgumentBuilder.Build(d, Input()).Errors.Should().ContainSingle().Which.Should().Contain("Ungültiger Parametername");
    }

    [Fact]
    public void EncodedBootstrap_DecodesToFixedScript()
    {
        Encoding.Unicode.GetString(Convert.FromBase64String(ScriptArgumentBuilder.EncodedBootstrap))
            .Should().Be(ScriptArgumentBuilder.Bootstrap);
        ScriptArgumentBuilder.Bootstrap.Should().Contain("& $__saScript @__saArgs");
    }
}
