using Microsoft.Agents.AI;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// SessionStateExtensions 的类型安全访问和集合初始化测试。
/// 针对 MAF AgentSessionStateBag 类型。
/// </summary>
public class SessionStateExtensionsTests
{
    private static AgentSessionStateBag NewStateBag() => new();

    [Fact]
    public void GetOrInitializeModifiedFiles_CaseInsensitive()
    {
        var bag = NewStateBag();
        var set = bag.GetOrInitializeModifiedFiles();
        set.Add("/Path/To/File.cs");
        set.Contains("/path/to/file.cs").Should().BeTrue();
    }

    // 跨字段独立性

    [Fact]
    public void AllFields_Independent_NoCrossContamination()
    {
        var bag = NewStateBag();

        bag.SetCurrentState(AgentState.Recovering);
        bag.IncrementConsecutiveFailures();
        bag.IncrementTotalToolCalls();
        bag.IncrementEditsSinceLastBuild();
        bag.GetOrInitializeRecentToolCalls().Add(new ToolCallRecord("Bash", null, true, DateTimeOffset.UtcNow, TimeSpan.Zero));
        bag.GetOrInitializeModifiedFiles().Add("/test.cs");
        var execCtx = bag.GetOrInitializeToolExecutionContext();
        execCtx.IsError = true;
        execCtx.Guidance = GuidanceKind.TaskRecovery;

        bag.GetCurrentState().Should().Be(AgentState.Recovering);
        bag.GetConsecutiveFailures().Should().Be(1);
        bag.GetTotalToolCalls().Should().Be(1);
        bag.GetEditsSinceLastBuild().Should().Be(1);
        bag.GetOrInitializeRecentToolCalls().Count.Should().Be(1);
        bag.GetOrInitializeModifiedFiles().Count.Should().Be(1);
        var execCtx2 = bag.GetOrInitializeToolExecutionContext();
        execCtx2.IsError.Should().BeTrue();
        execCtx2.Guidance.Should().Be(GuidanceKind.TaskRecovery);
    }

    // ToolExecutionContext

    [Fact]
    public void ResetToolExecutionContext_ClearsIsErrorAndGuidance()
    {
        var bag = NewStateBag();
        var ctx = bag.GetOrInitializeToolExecutionContext();
        ctx.IsError = true;
        ctx.Guidance = GuidanceKind.TaskRecovery;

        bag.ResetToolExecutionContext();

        var ctx2 = bag.GetOrInitializeToolExecutionContext();
        ctx2.IsError.Should().BeFalse();
        ctx2.Guidance.Should().Be(GuidanceKind.None);
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("askuserquestion")]
    [InlineData("AskUserQuestions")]
    [InlineData("askuserquestions")]
    public void StateMachineBlocked_AllowsUserInterventionTool(string toolName)
    {
        StateMachineMiddleware.ShouldBlockTool(AgentState.Blocked, toolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Bash")]
    [InlineData("Task")]
    public void StateMachineBlocked_RejectsNonInterventionTools(string toolName)
    {
        StateMachineMiddleware.ShouldBlockTool(AgentState.Blocked, toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("AskUserQuestions")]
    public void StateMachineTransition_BlockedUserAnswer_UnblocksSession(string toolName)
    {
        var bag = NewStateBag();
        bag.SetCurrentState(AgentState.Blocked);
        bag.SetConsecutiveFailures(3);

        StateMachine.Transition(bag, toolName, isSuccess: true);

        bag.GetCurrentState().Should().Be(AgentState.Active);
        bag.GetConsecutiveFailures().Should().Be(0);
    }

    [Fact]
    public void GetOrInitializeToolExecutionContext_ReturnsSameReference()
    {
        var bag = NewStateBag();
        var ctx1 = bag.GetOrInitializeToolExecutionContext();
        ctx1.IsError = true;

        var ctx2 = bag.GetOrInitializeToolExecutionContext();
        ctx2.Should().BeSameAs(ctx1);
        ctx2.IsError.Should().BeTrue();
    }
}
