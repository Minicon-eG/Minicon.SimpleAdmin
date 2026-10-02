using FluentAssertions;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ActiveProblemServerStaleTests
{
    [Fact]
    public void ServerStale_Unreachable_ProducesCriticalWithDeterministicId()
    {
        var problem = ActiveProblem.ServerStale("SERVER1", null, unreachable: true);

        problem.Id.Should().Be("server_server1_stale");
        problem.Type.Should().Be("server");
        problem.Severity.Should().Be(ProblemSeverity.Critical);
        problem.Message.Should().Contain("nicht erreichbar");
        problem.Context["unreachable"].Should().Be(true);
    }

    [Fact]
    public void ServerStale_StaleWithAge_MentionsMinutes()
    {
        var problem = ActiveProblem.ServerStale("SERVER2", 14.0, unreachable: false);

        problem.Id.Should().Be("server_server2_stale");
        problem.Severity.Should().Be(ProblemSeverity.Critical);
        problem.Message.Should().Contain("14");
    }
}
