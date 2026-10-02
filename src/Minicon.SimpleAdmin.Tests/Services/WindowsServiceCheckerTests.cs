using Minicon.SimpleAdmin.Checkers;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

public class WindowsServiceCheckerTests
{
    // ── MatchesWildcard ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("SQLWriter", "SQLWriter", true)]
    [InlineData("SQLWriter", "sqlwriter", true)]
    [InlineData("SQLWriter", "W3SVC", false)]
    public void MatchesWildcard_ExactName(string pattern, string name, bool expected)
    {
        WindowsServiceChecker.MatchesWildcard(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("SQL*", "SQLWriter", true)]
    [InlineData("SQL*", "SQLBrowser", true)]
    [InlineData("SQL*", "MSSQLSERVER", false)]
    [InlineData("SQL*", "W3SVC", false)]
    [InlineData("sql*", "SQLWriter", true)]
    public void MatchesWildcard_TrailingWildcard(string pattern, string name, bool expected)
    {
        WindowsServiceChecker.MatchesWildcard(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("*BizTalk*", "BizTalkServiceBroker", true)]
    [InlineData("*BizTalk*", "BizTalk", true)]
    [InlineData("*BizTalk*", "W3SVC", false)]
    [InlineData("*Service*", "BitsService", true)]
    [InlineData("*Service*", "ServiceHost", true)]
    [InlineData("*Service*", "W3SVC", false)]
    public void MatchesWildcard_ContainsWildcard(string pattern, string name, bool expected)
    {
        WindowsServiceChecker.MatchesWildcard(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("*", "AnyService", true)]
    [InlineData("*", "SQLWriter", true)]
    [InlineData("*", "W3SVC", true)]
    public void MatchesWildcard_GlobalWildcard(string pattern, string name, bool expected)
    {
        WindowsServiceChecker.MatchesWildcard(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("SQL?riter", "SQLWriter", true)]
    [InlineData("SQL?riter", "SQLBriter", true)]
    [InlineData("SQL?riter", "SQLWWriter", false)]
    public void MatchesWildcard_SingleCharWildcard(string pattern, string name, bool expected)
    {
        WindowsServiceChecker.MatchesWildcard(pattern, name).Should().Be(expected);
    }

    // ── ParseStartupType ─────────────────────────────────────────────────────

    [Fact]
    public void ParseStartupType_Automatic()
    {
        var output = """
            [SC] QueryServiceConfig SUCCESS

            SERVICE_NAME: SQLWriter
                    TYPE               : 10  WIN32_OWN_PROCESS
                    START_TYPE         : 2   AUTO_START
                    ERROR_CONTROL      : 1   NORMAL
            """;
        WindowsServiceChecker.ParseStartupType(output)
            .Should().Be(WindowsServiceChecker.StartupType.Automatic);
    }

    [Fact]
    public void ParseStartupType_AutomaticDelayed()
    {
        var output = """
            [SC] QueryServiceConfig SUCCESS

            SERVICE_NAME: Spooler
                    TYPE               : 110  WIN32_OWN_PROCESS  INTERACTIVE
                    START_TYPE         : 2   AUTO_START (DELAYED)
                    ERROR_CONTROL      : 1   NORMAL
            """;
        WindowsServiceChecker.ParseStartupType(output)
            .Should().Be(WindowsServiceChecker.StartupType.AutomaticDelayed);
    }

    [Fact]
    public void ParseStartupType_Manual()
    {
        var output = """
            [SC] QueryServiceConfig SUCCESS

            SERVICE_NAME: W3SVC
                    TYPE               : 20  WIN32_SHARE_PROCESS
                    START_TYPE         : 3   DEMAND_START
                    ERROR_CONTROL      : 1   NORMAL
            """;
        WindowsServiceChecker.ParseStartupType(output)
            .Should().Be(WindowsServiceChecker.StartupType.Manual);
    }

    [Fact]
    public void ParseStartupType_Disabled()
    {
        var output = """
            [SC] QueryServiceConfig SUCCESS

            SERVICE_NAME: Fax
                    TYPE               : 10  WIN32_OWN_PROCESS
                    START_TYPE         : 4   DISABLED
                    ERROR_CONTROL      : 0   IGNORE
            """;
        WindowsServiceChecker.ParseStartupType(output)
            .Should().Be(WindowsServiceChecker.StartupType.Disabled);
    }

    [Fact]
    public void ParseStartupType_UnknownWhenNoStartTypeLine()
    {
        var output = "[SC] OpenService FAILED 1060:\r\nThe specified service does not exist as an installed service.";
        WindowsServiceChecker.ParseStartupType(output)
            .Should().Be(WindowsServiceChecker.StartupType.Unknown);
    }
}
