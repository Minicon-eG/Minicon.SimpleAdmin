using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class DataDirectoryWarningTests
{
    [Fact]
    public void DefaultDataDirectoriesUnderAppBase_LogOneGermanWarningWithRemediation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigPath"] = "" })
            .Build();
        var logger = new CaptureLogger();

        SimpleAdminWebUIExtensions.WarnAboutApplicationDataDirectories(configuration, Path.GetTempPath(), logger);

        logger.WarningCount.Should().Be(1);
        logger.LastWarning.Should().Contain("ConfigPath")
            .And.Contain("ScriptRunner:RunLogDirectory")
            .And.Contain("ScriptRunner:UploadDirectory")
            .And.Contain("ScheduledTasks:AuditLogDirectory")
            .And.Contain("BizTalk:AuditLogDirectory")
            .And.Contain("C:\\ProgramData\\SimpleAdmin")
            .And.Contain("kopieren");
    }

    [Fact]
    public void DataDirectoriesOutsideApplication_DoNotWarn()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa-outside-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigPath"] = Path.Combine(root, "data") })
            .Build();
        var logger = new CaptureLogger();

        SimpleAdminWebUIExtensions.WarnAboutApplicationDataDirectories(configuration, Path.Combine(root, "app"), logger);

        logger.WarningCount.Should().Be(0);
    }

    [Fact]
    public void RelativeConfigPath_IsWarnedAsUnsafe()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigPath"] = "../data" })
            .Build();
        var logger = new CaptureLogger();

        SimpleAdminWebUIExtensions.WarnAboutApplicationDataDirectories(configuration,
            Path.Combine(Path.GetTempPath(), "sa-content-" + Guid.NewGuid().ToString("N")), logger);

        logger.WarningCount.Should().Be(1);
        logger.LastWarning.Should().Contain("ConfigPath").And.Contain("relativ");
    }

    private sealed class CaptureLogger : ILogger
    {
        public int WarningCount { get; private set; }
        public string? LastWarning { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            WarningCount++;
            LastWarning = formatter(state, exception);
        }
    }
}
