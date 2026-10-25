using Microsoft.Agents.AI;
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
        bag.GetOrInitializeModifiedFiles().Add("/test.cs");
        var execCtx = bag.GetOrInitializeToolExecutionContext();
        execCtx.IsError = true;
        execCtx.Guidance = GuidanceKind.TaskRecovery;

        bag.GetCurrentState().Should().Be(AgentState.Recovering);
        bag.GetConsecutiveFailures().Should().Be(1);
        bag.GetTotalToolCalls().Should().Be(1);
        bag.GetEditsSinceLastBuild().Should().Be(1);
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
    [InlineData("SubmitPlan")]
    [InlineData("submitplan")]
    public void StateMachineBlocked_AllowsUserInterventionAndPlanSubmissionTools(string toolName)
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
    [InlineData("SubmitPlan")]
    public void StateMachineTransition_BlockedEscapeTool_UnblocksSession(string toolName)
    {
        var bag = NewStateBag();
        bag.SetCurrentState(AgentState.Blocked);
        bag.SetConsecutiveFailures(3);
        bag.SetLastFailedToolName("WebFetch");
        bag.SetSameToolConsecutiveFailures(3);

        StateMachine.Transition(bag, toolName, isSuccess: true);

        bag.GetCurrentState().Should().Be(AgentState.Active);
        bag.GetConsecutiveFailures().Should().Be(0);
        bag.GetLastFailedToolName().Should().BeNull();
        bag.GetSameToolConsecutiveFailures().Should().Be(0);
    }

    [Fact]
    public void StateMachine_SameToolConsecutiveFailures_BlocksOnlyThatTool_AllowsCrossToolRecovery()
    {
        var bag = NewStateBag();

        // 同一工具（如 WebFetch）连续失败 3 次
        StateMachine.Transition(bag, "WebFetch", isSuccess: false);
        StateMachine.Transition(bag, "WebFetch", isSuccess: false);
        StateMachine.Transition(bag, "WebFetch", isSuccess: false);

        bag.GetCurrentState().Should().Be(AgentState.Recovering);
        bag.GetSameToolConsecutiveFailures().Should().Be(3);

        // 再次调用同一故障工具 WebFetch 被定向阻拦
        StateMachineMiddleware.ShouldBlockTool(bag, "WebFetch", out var blockReason).Should().BeTrue();
        blockReason.Should().Contain("WebFetch").And.Contain("temporarily blocked");

        // 但切换到其他工具（如 Read / Grep / SubmitPlan）自愈时不被阻拦
        StateMachineMiddleware.ShouldBlockTool(bag, "Read", out _).Should().BeFalse();
        StateMachineMiddleware.ShouldBlockTool(bag, "Grep", out _).Should().BeFalse();
        StateMachineMiddleware.ShouldBlockTool(bag, "SubmitPlan", out _).Should().BeFalse();

        // 切换到其他工具执行成功后，自动回到 Active 并清空单工具锁定
        StateMachine.Transition(bag, "Read", isSuccess: true);
        bag.GetCurrentState().Should().Be(AgentState.Active);
        bag.GetConsecutiveFailures().Should().Be(0);
        bag.GetSameToolConsecutiveFailures().Should().Be(0);
        bag.GetLastFailedToolName().Should().BeNull();
        StateMachineMiddleware.ShouldBlockTool(bag, "WebFetch", out _).Should().BeFalse();
    }

    [Fact]
    public void StateMachine_VerificationFailure_EscalatesToBlocked()
    {
        var bag = NewStateBag();

        // 编译/验证失败立即进入 Recovering
        StateMachine.Transition(bag, "Edit", isSuccess: false, isVerificationFailure: true);
        bag.GetCurrentState().Should().Be(AgentState.Recovering);

        // 继续验证失败累计达阈值后升级为全局 Blocked
        StateMachine.Transition(bag, "Edit", isSuccess: false, isVerificationFailure: true);
        StateMachine.Transition(bag, "Edit", isSuccess: false, isVerificationFailure: true);
        bag.GetCurrentState().Should().Be(AgentState.Blocked);

        // ResetStateMachineForNewTurn 在新一轮用户交互启动时解锁
        bag.ResetStateMachineForNewTurn();
        bag.GetCurrentState().Should().Be(AgentState.Active);
        bag.GetConsecutiveFailures().Should().Be(0);
        bag.GetSameToolConsecutiveFailures().Should().Be(0);
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
