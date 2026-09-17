using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

public sealed class TeamRunErrorsTests
{
    [Fact]
    public void Fail_EmitsErrorEventAndReturnsFailedResult()
    {
        OrchestrationEvent? seen = null;
        var result = TeamRunErrors.Fail("impl", "boom", e => seen = e);

        result.TeamName.Should().Be("impl");
        result.HadFailures.Should().BeFalse(); // Fail factory does not set HadFailures; matches prior TeamError
        result.Error.Should().NotBeNull();
        result.Error!.Detail.Should().Be("boom");
        result.Output.Should().Be("boom");
        result.TurnsCompleted.Should().Be(0);
        seen.Should().BeOfType<OrchestrationEvent.Error>();
        ((OrchestrationEvent.Error)seen!).Message.Should().Be("boom");
    }
}
