using OneCode.Core.Config;
using OneCode.Core.Goals;
using OneCode.Core.PlanMode;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Tests;

/// <summary>
/// 内核共享词汇防回归：RunTerminalReasonMap 终结原因映射、
/// CommandIdempotency 命令幂等、ModeBudgetSettings 模式预算统一视图。
/// 映射变化即跨模式语义变化，由本文件锚定。
/// </summary>
public sealed class WorkflowKernelVocabularyTests
{
    [Theory]
    [InlineData(GoalRunState.Completed, RunTerminalReason.Completed)]
    [InlineData(GoalRunState.Paused, RunTerminalReason.BudgetExceeded)]
    [InlineData(GoalRunState.Cancelled, RunTerminalReason.Cancelled)]
    [InlineData(GoalRunState.Blocked, RunTerminalReason.Blocked)]
    [InlineData(GoalRunState.Failed, RunTerminalReason.ValidationFailed)]
    public void RunTerminalReasonMap_FromGoalStateMapsTerminalStates(GoalRunState state, RunTerminalReason expected)
        => RunTerminalReasonMap.FromGoalState(state).Should().Be(expected);

    [Fact]
    public void RunTerminalReasonMap_FromGoalState_NonTerminalFallsBackToAgentException()
        => RunTerminalReasonMap.FromGoalState(GoalRunState.Executing).Should().Be(RunTerminalReason.AgentException);
}

/// <summary>CommandId 幂等内核能力防回归（Plan 聚合实现 ICommandIdempotent）。</summary>
public sealed class CommandIdempotencyTests
{
    [Fact]
    public void IsReplay_MatchesLastProcessedCommandId()
    {
        var workflow = PlanWorkflow.Create(new SessionId("session-1"));
        var processed = workflow with { LastProcessedCommandId = "cmd-42" };

        CommandIdempotency.IsReplay(processed, "cmd-42").Should().BeTrue();
        CommandIdempotency.IsReplay(processed, "cmd-43").Should().BeFalse();
    }

    [Fact]
    public void IsReplay_MissingAggregateOrFreshWorkflowIsNeverReplay()
    {
        var fresh = PlanWorkflow.Create(new SessionId("session-2"));

        CommandIdempotency.IsReplay(null, "cmd-42").Should().BeFalse();
        CommandIdempotency.IsReplay(fresh, "cmd-42").Should().BeFalse();
    }
}

/// <summary>模式预算统一视图（ModeBudgetSettings）防回归。</summary>
public sealed class ModeBudgetSettingsTests
{
    [Fact]
    public void FromSettings_CarriesTurnLimitAndGoalDimensions()
    {
        var settings = new AppSettings(new Dictionary<string, object?>
        {
            ["maxTurns"] = 66,
        });

        var budget = ModeBudgetSettings.FromSettings(settings);

        budget.MaxTurns.Should().Be(66);
        // Goal 全开：三级预算模型三维度均生效。
        budget.ToGoalBudget().MaxSubGoalAttempts.Should().Be(20);
        budget.ToGoalBudget().MaxTotalTokens.Should().Be(200_000);
        // maxWallClockHours 未配置时取内置默认 2.0 小时。
        budget.ToGoalBudget().MaxWallClock.Should().Be(TimeSpan.FromHours(2.0));
    }

    [Fact]
    public void FromSettings_MissingKeysFallBackToBuiltInDefaults()
    {
        var budget = ModeBudgetSettings.FromSettings(new AppSettings());

        budget.MaxTurns.Should().Be(CoreConstants.Session.MaxTurnsDefault);
        budget.ToGoalBudget().MaxSubGoalAttempts.Should().Be(20);
        budget.ToGoalBudget().MaxWallClock.Should().Be(TimeSpan.FromHours(2.0), "墙钟默认上限 2 小时");
    }
}
