using FluentAssertions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>v2.25.0: acknowledge "bis Datum"/"unbegrenzt" and central event log ignore rules.</summary>
public class AcknowledgeUntilAndCentralIgnoreTests
{
    // ── Acknowledge ─────────────────────────────────────────────────────────

    [Fact]
    public void Create_WithEndDate_UsesItAsUtcExpiry()
    {
        var until = DateTime.UtcNow.AddDays(3);

        var ack = Acknowledge.Create("srv", "p1", "tester", "", durationMinutes: 60, expiresAt: until);

        ack.ExpiresAt.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        ack.Options.DurationMinutes.Should().BeGreaterThan(60 * 24 * 2);
    }

    [Fact]
    public void Create_ZeroDurationWithoutEndDate_IsIndefinite()
    {
        Acknowledge.Create("srv", "p1", "tester", "", durationMinutes: 0).ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void ApplyAcknowledges_IgnoresExpiredEntries_AndCopiesExpiryAndUser()
    {
        var expired = Acknowledge.Create("srv", "p-expired", "a", "", 60);
        expired.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); // still "Active" until a worker processes it
        var indefinite = Acknowledge.Create("srv", "p-forever", "DOMAIN\\user", "", 0);
        var problems = new List<ActiveProblem>
        {
            new() { Id = "p-expired" },
            new() { Id = "p-forever" }
        };

        ProblemDerivationService.ApplyAcknowledges(problems, new[] { expired, indefinite }, "srv");

        problems[0].Acknowledged.Should().BeFalse();
        problems[1].Acknowledged.Should().BeTrue();
        problems[1].AcknowledgeExpiresAt.Should().BeNull();
        problems[1].AcknowledgedBy.Should().Be("DOMAIN\\user");
    }

    // ── Central event log ignore ────────────────────────────────────────────

    private static EventLogState State(MetricStatus status, int count, params (string Source, int Id)[] events) => new()
    {
        Status = status,
        EventCount = count,
        RecentEvents = events.Select(e => new EventLogEntry { Source = e.Source, EventId = e.Id, Level = "Error", Message = "x" }).ToList()
    };

    private static readonly EventLogDefaults Defaults = new() { WarningCount = 5, CriticalCount = 20 };

    [Fact]
    public void ApplyCentral_AllEventsIgnored_ClearsCountAndStatus()
    {
        var state = State(MetricStatus.Warning, 6, ("BizTalk", 5410), ("BizTalk", 5410), ("BizTalk", 5410), ("BizTalk", 5410), ("BizTalk", 5410), ("BizTalk", 5410));
        var rules = new List<EventLogIgnoreRule> { new() { Servers = { "SRV1" }, Source = "BizTalk", EventIds = { 5410 } } };

        var removed = EventLogIgnoreMatcher.ApplyCentral(state, "srv1", rules, Defaults, DateTime.UtcNow);

        removed.Should().Be(6);
        state.EventCount.Should().Be(0);
        state.Status.Should().Be(MetricStatus.Healthy);
        state.IgnoredCount.Should().Be(6);
    }

    [Fact]
    public void ApplyCentral_OtherServerOrExpiredRule_ChangesNothing()
    {
        var rules = new List<EventLogIgnoreRule>
        {
            new() { Servers = { "other" }, Source = "BizTalk" },
            new() { Source = "BizTalk", ExpiresAt = DateTime.UtcNow.AddMinutes(-5) }
        };
        var state = State(MetricStatus.Warning, 5, ("BizTalk", 1), ("BizTalk", 2));

        EventLogIgnoreMatcher.ApplyCentral(state, "srv1", rules, Defaults, DateTime.UtcNow).Should().Be(0);
        state.Status.Should().Be(MetricStatus.Warning);
        state.EventCount.Should().Be(5);
    }

    [Fact]
    public void ApplyCentral_PartiallyIgnored_LowersButNeverEscalates()
    {
        var state = State(MetricStatus.Critical, 22, ("Noise", 1), ("Noise", 1), ("Noise", 1), ("Real", 9));
        var rules = new List<EventLogIgnoreRule> { new() { Source = "Noise" } };

        EventLogIgnoreMatcher.ApplyCentral(state, "srv1", rules, Defaults, DateTime.UtcNow);

        state.EventCount.Should().Be(19);
        state.Status.Should().Be(MetricStatus.Warning);
        state.RecentEvents.Should().ContainSingle(e => e.Source == "Real");
    }
}
