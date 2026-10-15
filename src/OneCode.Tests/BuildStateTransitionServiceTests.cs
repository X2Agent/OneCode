// BuildStateTransitionService 的守卫测试：Verifying/Recovering 必须能转入
// 预算/轮次耗尽终态——CompleteAsync 先把状态推进到 Verifying，之后才检查
// result.TerminalReason，缺边会让预算熔断路径抛 InvalidOperationException。
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Domain;

namespace OneCode.Tests;

public sealed class BuildStateTransitionServiceTests
{
    [Theory]
    [InlineData(BuildRunState.Verifying, BuildRunState.BudgetExceeded)]
    [InlineData(BuildRunState.Verifying, BuildRunState.LimitReached)]
    [InlineData(BuildRunState.Recovering, BuildRunState.BudgetExceeded)]
    [InlineData(BuildRunState.Recovering, BuildRunState.LimitReached)]
    public void Transition_VerifyingOrRecovering_ToBudgetOrTurnLimit_Succeeds(
        BuildRunState from, BuildRunState target)
    {
        var sut = new BuildStateTransitionService();
        var run = CreateRun(from);

        var next = sut.Transition(run, target, DateTimeOffset.UtcNow);

        next.State.Should().Be(target);
        next.SequenceNumber.Should().Be(run.SequenceNumber + 1);
        next.UpdatedAt.Should().BeOnOrAfter(run.UpdatedAt);
    }

    [Fact]
    public void Transition_Recovering_ToAccepting_SkippingVerification_StillRejected()
    {
        // 反向守卫：补边只放行终态，不得打开 Recovering → Accepting 的捷径（绕过验证收口）。
        var sut = new BuildStateTransitionService();

        var act = () => sut.Transition(CreateRun(BuildRunState.Recovering), BuildRunState.Accepting, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Illegal BuildRun transition: Recovering -> Accepting*");
    }

    [Fact]
    public void Transition_Verifying_ToBudgetExceeded_PreservesAggregateData()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateRun(BuildRunState.Verifying) with
        {
            ChangedFiles = ["src/Foo.cs"],
            Metrics = new BuildRunMetrics(TurnsCompleted: 3, ToolCalls: 0, InputTokens: 1_000, OutputTokens: 200, Duration: null),
        };

        var next = sut.Transition(run, BuildRunState.BudgetExceeded, DateTimeOffset.UtcNow);

        next.Id.Should().Be(run.Id);
        next.ChangedFiles.Should().Equal(run.ChangedFiles);
        next.Metrics.TurnsCompleted.Should().Be(3);
    }

    private static BuildRun CreateRun(BuildRunState state) => new()
    {
        Id = BuildRunId.New(),
        ConversationId = SessionId.NewId(),
        State = state,
        IntakePrompt = "Fix Foo.cs and run tests.",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        SequenceNumber = 7,
    };
}
