using FluentAssertions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Tests für die Event-Log-Ignorier-Regeln: Wildcard-Matching, Server-Scope,
/// Kriterien-Kombination, Ablaufdatum und Quell-Filter (Include/Exclude).
/// </summary>
public class EventLogIgnoreRuleTests
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

    // ── Wildcard-Matching ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Microsoft-Windows-DistributedCOM", "Microsoft-Windows-DistributedCOM", true)]
    [InlineData("Microsoft-Windows-DistributedCOM", "microsoft-windows-distributedcom", true)] // case-insensitive
    [InlineData("Microsoft-Windows-DistributedCOM", "Microsoft-Windows-*", true)]
    [InlineData("Schannel", "*chann*", true)]
    [InlineData("Schannel", "Perflib", false)]
    [InlineData("WEB01", "web0*", true)]
    [InlineData("DB01", "web0*", false)]
    [InlineData("srv1", "srv?", true)]
    [InlineData("srv12", "srv?", false)]
    public void MatchesWildcard_matches_expected(string value, string pattern, bool expected)
    {
        EventLogChecker.MatchesWildcard(value, pattern).Should().Be(expected);
    }

    // ── Server-Scope ─────────────────────────────────────────────────────────

    [Fact]
    public void RuleAppliesToServer_empty_list_applies_to_all()
    {
        var rule = new EventLogIgnoreRule();
        EventLogChecker.RuleAppliesToServer(rule, "anyserver").Should().BeTrue();
    }

    [Fact]
    public void RuleAppliesToServer_matches_named_server_case_insensitive()
    {
        var rule = new EventLogIgnoreRule { Servers = new List<string> { "SRV-A", "SRV-B" } };
        EventLogChecker.RuleAppliesToServer(rule, "srv-b").Should().BeTrue();
        EventLogChecker.RuleAppliesToServer(rule, "srv-c").Should().BeFalse();
    }

    [Fact]
    public void RuleAppliesToServer_supports_wildcards()
    {
        var rule = new EventLogIgnoreRule { Servers = new List<string> { "web0*" } };
        EventLogChecker.RuleAppliesToServer(rule, "WEB01").Should().BeTrue();
        EventLogChecker.RuleAppliesToServer(rule, "DB01").Should().BeFalse();
    }

    // ── IsEventIgnored: Kriterien ────────────────────────────────────────────

    [Fact]
    public void IsEventIgnored_matches_source_and_eventId()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "Microsoft-Windows-DistributedCOM", EventIds = new List<int> { 10016 } }
        };

        EventLogChecker.IsEventIgnored("Microsoft-Windows-DistributedCOM", 10016, "msg", "System", rules, Now)
            .Should().BeTrue();
        EventLogChecker.IsEventIgnored("Microsoft-Windows-DistributedCOM", 10010, "msg", "System", rules, Now)
            .Should().BeFalse("andere Event-ID");
        EventLogChecker.IsEventIgnored("Schannel", 10016, "msg", "System", rules, Now)
            .Should().BeFalse("andere Quelle");
    }

    [Fact]
    public void IsEventIgnored_matches_message_substring_case_insensitive()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "MyApp", MessageContains = "connection reset" }
        };

        EventLogChecker.IsEventIgnored("MyApp", 1, "Outer Connection RESET by peer", "Application", rules, Now)
            .Should().BeTrue();
        EventLogChecker.IsEventIgnored("MyApp", 1, "timeout", "Application", rules, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsEventIgnored_message_supports_wildcards()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "MyApp", MessageContains = "job * finished with warnings" }
        };

        EventLogChecker.IsEventIgnored("MyApp", 1, "job cleanup-42 finished with warnings", "Application", rules, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsEventIgnored_respects_logName_scope()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "Schannel", LogName = "System" }
        };

        EventLogChecker.IsEventIgnored("Schannel", 36887, "msg", "System", rules, Now).Should().BeTrue();
        EventLogChecker.IsEventIgnored("Schannel", 36887, "msg", "Application", rules, Now)
            .Should().BeFalse("Regel ist auf das System-Log begrenzt");
    }

    [Fact]
    public void IsEventIgnored_eventId_only_rule_matches_any_source()
    {
        var rules = new List<EventLogIgnoreRule> { new() { EventIds = new List<int> { 10016 } } };

        EventLogChecker.IsEventIgnored("AnySource", 10016, "msg", "System", rules, Now).Should().BeTrue();
        EventLogChecker.IsEventIgnored("AnySource", 1, "msg", "System", rules, Now).Should().BeFalse();
    }

    // ── Ablauf & leere Regeln ────────────────────────────────────────────────

    [Fact]
    public void IsEventIgnored_expired_rule_no_longer_suppresses()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "Schannel", ExpiresAt = Now.AddMinutes(-1) }
        };

        EventLogChecker.IsEventIgnored("Schannel", 36887, "msg", "System", rules, Now)
            .Should().BeFalse("abgelaufene Regeln erzwingen eine Wiedervorlage");
    }

    [Fact]
    public void IsEventIgnored_not_yet_expired_rule_suppresses()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Source = "Schannel", ExpiresAt = Now.AddMinutes(1) }
        };

        EventLogChecker.IsEventIgnored("Schannel", 36887, "msg", "System", rules, Now).Should().BeTrue();
    }

    [Fact]
    public void IsEventIgnored_rule_without_criteria_is_inactive()
    {
        // Eine Regel ohne Source/EventIds/Message würde ALLES unterdrücken — inaktiv behandeln
        var rules = new List<EventLogIgnoreRule> { new() { Reason = "leer", LogName = "System" } };

        EventLogChecker.IsEventIgnored("AnySource", 1, "msg", "System", rules, Now).Should().BeFalse();
    }

    // ── Include/Exclude-Sources (per Log) ────────────────────────────────────

    [Fact]
    public void MatchesSourceFilters_no_filters_passes_everything()
    {
        EventLogChecker.MatchesSourceFilters("Any", null, null).Should().BeTrue();
        EventLogChecker.MatchesSourceFilters("Any", new List<string>(), new List<string>()).Should().BeTrue();
    }

    [Fact]
    public void MatchesSourceFilters_include_list_restricts()
    {
        var include = new List<string> { "MSSQL*" };
        EventLogChecker.MatchesSourceFilters("MSSQLSERVER", include, null).Should().BeTrue();
        EventLogChecker.MatchesSourceFilters("Schannel", include, null).Should().BeFalse();
    }

    [Fact]
    public void MatchesSourceFilters_exclude_wins_over_include()
    {
        var include = new List<string> { "*" };
        var exclude = new List<string> { "*Test*" };
        EventLogChecker.MatchesSourceFilters("MyTestSource", include, exclude).Should().BeFalse();
        EventLogChecker.MatchesSourceFilters("MySource", include, exclude).Should().BeTrue();
    }

    // ── Form-Binding-Helfer ──────────────────────────────────────────────────

    [Fact]
    public void EventIdsText_roundtrip_parses_and_formats()
    {
        var rule = new EventLogIgnoreRule { EventIdsText = " 10016, 36887; abc, 7031 " };
        rule.EventIds.Should().Equal(10016, 36887, 7031);
        rule.EventIdsText.Should().Be("10016,36887,7031");
    }

    [Fact]
    public void ServersText_roundtrip_parses_and_formats()
    {
        var rule = new EventLogIgnoreRule { ServersText = " srv-a , web0* " };
        rule.Servers.Should().Equal("srv-a", "web0*");
        rule.ServersText.Should().Be("srv-a, web0*");
    }
}
