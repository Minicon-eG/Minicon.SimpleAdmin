using FluentAssertions;
using Minicon.SimpleAdmin.WebUI.Hosting;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class InstallerOptionsTests
{
    [Fact]
    public void Parse_defaults_match_documented_values()
    {
        var o = InstallerOptions.Parse(Array.Empty<string>());

        o.ServiceName.Should().Be("SimpleAdminWebUI");
        o.Port.Should().Be(5080);
        o.KeysDirectory.Should().Be(@"C:\ProgramData\SimpleAdmin\dp-keys");
        o.EffectivePrincipal.Should().Be(@"NT AUTHORITY\SYSTEM");
        o.UrlAcl.Should().Be("http://+:5080/");
        o.NonInteractive.Should().BeFalse();
        o.Purge.Should().BeFalse();
    }

    [Fact]
    public void Parse_accepts_all_supported_flags()
    {
        var o = InstallerOptions.Parse(new[]
        {
            "--port=8080",
            @"--account=CORP\svc-sa",
            "--password=secret",
            "--service-name=SA-WebUI",
            "--display-name=Minicon SA",
            "--description=Admin UI",
            @"--keys-dir=D:\keys",
            "--non-interactive",
            "--purge"
        });

        o.Port.Should().Be(8080);
        o.Account.Should().Be(@"CORP\svc-sa");
        o.Password.Should().Be("secret");
        o.ServiceName.Should().Be("SA-WebUI");
        o.DisplayName.Should().Be("Minicon SA");
        o.Description.Should().Be("Admin UI");
        o.KeysDirectory.Should().Be(@"D:\keys");
        o.NonInteractive.Should().BeTrue();
        o.Purge.Should().BeTrue();
        o.EffectivePrincipal.Should().Be(@"CORP\svc-sa");
        o.UrlAcl.Should().Be("http://+:8080/");
    }

    [Theory]
    [InlineData("--port=0")]
    [InlineData("--port=70000")]
    [InlineData("--port=abc")]
    public void Parse_rejects_invalid_port(string arg)
    {
        FluentActions.Invoking(() => InstallerOptions.Parse(new[] { arg }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_rejects_unknown_flag()
    {
        FluentActions.Invoking(() => InstallerOptions.Parse(new[] { "--unknown" }))
            .Should().Throw<ArgumentException>();
    }
}

public sealed class ServiceInstallerDispatchTests
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(string FileName, string[] Args)> Calls { get; } = new();
        public Dictionary<string, ProcessResult> Overrides { get; } = new();

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            var args = arguments.ToArray();
            Calls.Add((fileName, args));
            var key = fileName + " " + string.Join(" ", args);
            foreach (var kv in Overrides)
                if (key.Contains(kv.Key)) return kv.Value;
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void Dispatch_rejects_unknown_command()
    {
        if (!OperatingSystem.IsWindows()) return; // installer rejects early on non-Windows

        var writer = new StringWriter();
        var runner = new RecordingRunner();
        var code = ServiceInstaller.Dispatch("foo", Array.Empty<string>(), @"C:\x.exe", writer, runner);
        code.Should().Be(2);
        writer.ToString().Should().Contain("Unbekanntes Kommando");
    }

    [Fact]
    public void Dispatch_on_non_windows_returns_platform_error()
    {
        if (OperatingSystem.IsWindows()) return;

        var writer = new StringWriter();
        var code = ServiceInstaller.Dispatch("install", Array.Empty<string>(), "/x", writer);
        code.Should().Be(2);
        writer.ToString().Should().Contain("Windows");
    }

    [Fact]
    public void Install_core_runs_expected_commands_in_order()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = Path.GetTempFileName();
        try
        {
            var writer = new StringWriter();
            var runner = new RecordingRunner();
            var opts = InstallerOptions.Parse(new[] { "--port=9090", @"--keys-dir=" + Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });

            var code = ServiceInstaller.Install(opts, exe, writer, runner);
            code.Should().Be(0);

            runner.Calls.Select(c => c.FileName).Should().Contain(new[] { "sc.exe", "netsh", "icacls" });
            runner.Calls.Should().Contain(c => c.FileName == "sc.exe" && c.Args.Contains("create"));
            runner.Calls.Should().Contain(c => c.FileName == "netsh" && c.Args.Contains("urlacl") && c.Args.Any(a => a.Contains("9090")));
            runner.Calls.Should().Contain(c => c.FileName == "sc.exe" && c.Args.Contains("start"));
        }
        finally
        {
            if (File.Exists(exe)) File.Delete(exe);
        }
    }

    [Fact]
    public void Uninstall_core_tears_down_without_touching_keys_dir_by_default()
    {
        if (!OperatingSystem.IsWindows()) return;

        var keysDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keysDir);
        try
        {
            var writer = new StringWriter();
            var runner = new RecordingRunner();
            var opts = InstallerOptions.Parse(new[] { "--port=9090", $"--keys-dir={keysDir}" });

            var code = ServiceInstaller.Uninstall(opts, writer, runner);
            code.Should().Be(0);

            runner.Calls.Should().Contain(c => c.FileName == "sc.exe" && c.Args.Contains("stop"));
            runner.Calls.Should().Contain(c => c.FileName == "sc.exe" && c.Args.Contains("delete"));
            Directory.Exists(keysDir).Should().BeTrue("ohne --purge bleibt der Keys-Ordner stehen");
        }
        finally
        {
            if (Directory.Exists(keysDir)) Directory.Delete(keysDir, true);
        }
    }
}
