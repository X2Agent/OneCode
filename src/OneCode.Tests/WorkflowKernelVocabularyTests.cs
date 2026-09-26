using OneCode.Core.Config;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.PlanMode;
using OneCode.Core.Workflows;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Tests;

/// <summary>
/// 内核共享词汇防回归：RunTerminalReasonMap 终结原因映射、
/// CommandIdempotency 命令幂等、ModeBudgetSettings 模式预算统一视图。
/// 映射变化即跨模式语义变化，由本文件锚定。
/// </summary>
public sealed class WorkflowKernelVocabularyTests
{
    // ---------- RunTerminalReasonMap ----------

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

    [Theory]
    [InlineData(TeamRunStatus.Succeeded, false, RunTerminalReason.Completed)]
    [InlineData(TeamRunStatus.Cancelled, false, RunTerminalReason.Cancelled)]
    [InlineData(TeamRunStatus.Blocked, false, RunTerminalReason.Blocked)]
    [InlineData(TeamRunStatus.Failed, true, RunTerminalReason.ValidationFailed)]
    [InlineData(TeamRunStatus.RolledBack, true, RunTerminalReason.ValidationFailed)]
    public void RunTerminalReasonMap_FromTeamStatusMapsTerminalStates(
        TeamRunStatus status, bool hasError, RunTerminalReason expected)
        => RunTerminalReasonMap.FromTeamStatus(status, hasError).Should().Be(expected);

    [Fact]
    public void RunTerminalReasonMap_FromTeamStatus_ErrorBeatsDefaultButNotFailed()
    {
        // 非终态 + 携带错误 → AgentException（原 { Error: not null } 分支）。
        RunTerminalReasonMap.FromTeamStatus(TeamRunStatus.Created, hasError: true)
            .Should().Be(RunTerminalReason.AgentException);
        // 非终态无错误 → 兜底 Completed。
        RunTerminalReasonMap.FromTeamStatus(TeamRunStatus.Created)
            .Should().Be(RunTerminalReason.Completed);
    }
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