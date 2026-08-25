using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.App.Services.BuildMode;

namespace OneCode.Tests;

/// <summary>
/// BuildResumePolicy 特征测试：锚定非终态 BuildRun 在 BeginOrResume 时的恢复路径裁决。
/// 这些规则原先内联在 BuildRunCoordinator.BeginOrResumeAsync 的 if 链中，
/// 提取为纯函数后由此文件防回归——任何映射变化都是用户可见的恢复行为变化。
/// </summary>
public sealed class BuildResumePolicyTests
{
    private static readonly string Fingerprint = "fp-current";

    private static BuildRun Run(BuildRunState state, string? fingerprint = null, BuildPlan? plan = null) => new()
    {
        Id = BuildRunId.New(),
        ConversationId = SessionId.NewId(),
        State = state,
        WorkspaceFingerprint = fingerprint ?? Fingerprint,
        Plan = plan,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BuildPlan Plan(string summary = "s", params string[] taskIds) => new(
        Summary: summary,
        Tasks: [.. taskIds.Select(id => new BuildPlanTask(
            Id: id,
            Title: id,
            Description: string.Empty,
            DependsOn: [],
            ExpectedFiles: [],
            AcceptanceCriteria: []))],
        ValidationCommands: [],
        Risks: [],
        NonGoals: []);

    // 指纹门禁

    [Fact]
    public void Evaluate_CheckpointFingerprintDrift_Blocks()
    {
        var run = Run(BuildRunState.Implementing, fingerprint: "fp-old");

        var d = BuildResumePolicy.Evaluate(run, prescribedPlan: null, Fingerprint);

        d.Action.Should().Be(BuildResumeAction.FingerprintDrift);
        d.FailureSummary.Should().Contain("re-baselining");
    }

    [Fact]
    public void Evaluate_AcceptingFingerprintDrift_UsesCommitBaselineMessage()
    {
        // Accepting 态基线是提交时指纹而非检查点指纹：提交后工作区允许继续变化
        var run = Run(BuildRunState.Accepting, fingerprint: "fp-checkpoint") with
        {
            CommitWorkspaceFingerprint = "fp-commit",
        };

        var d = BuildResumePolicy.Evaluate(run, prescribedPlan: null, currentFingerprint: "fp-other");

        d.Action.Should().Be(BuildResumeAction.FingerprintDrift);
        d.FailureSummary.Should().Contain("manual reconciliation");
    }

    [Fact]
    public void Evaluate_Accepting_CommitsAgainstCommitFingerprint_PassesGate()
    {
        var run = Run(BuildRunState.Accepting, fingerprint: "fp-checkpoint") with
        {
            CommitWorkspaceFingerprint = Fingerprint,
        };

        BuildResumePolicy.Evaluate(run, null, Fingerprint)
            .Action.Should().Be(BuildResumeAction.ConfirmCommit);
    }

    // 计划冲突（优先于指纹检查）

    [Fact]
    public void Evaluate_PrescribedPlanConflictsWithPersisted_PlanConflictWinsOverDrift()
    {
        var persisted = Plan("summary-a", "t1");
        var run = Run(BuildRunState.Planned, fingerprint: "fp-old", plan: persisted);

        var d = BuildResumePolicy.Evaluate(run, Plan("summary-b", "t1"), Fingerprint);

        // 原实现中冲突检查在指纹检查之前——保持该优先级不变
        d.Action.Should().Be(BuildResumeAction.PlanConflict);
    }

    [Fact]
    public void Evaluate_SamePlan_NoConflict()
    {
        var plan = Plan("summary", "t1", "t2");
        var run = Run(BuildRunState.Planned, plan: plan);

        BuildResumePolicy.Evaluate(run, Plan("summary", "t1", "t2"), Fingerprint)
            .Action.Should().Be(BuildResumeAction.ParkAtGate);
    }

    // 状态 → 动作映射

    [Theory]
    [InlineData(BuildRunState.Accepting, BuildResumeAction.ConfirmCommit)]
    [InlineData(BuildRunState.Intake, BuildResumeAction.ContinueAssessment)]
    [InlineData(BuildRunState.Assessing, BuildResumeAction.ContinueAssessment)]
    [InlineData(BuildRunState.ScopeConfirmed, BuildResumeAction.PrepareFromScope)]
    [InlineData(BuildRunState.Planning, BuildResumeAction.PrepareFromScope)]
    [InlineData(BuildRunState.Planned, BuildResumeAction.ParkAtGate)]
    [InlineData(BuildRunState.Implementing, BuildResumeAction.ParkAtGate)]
    [InlineData(BuildRunState.Verifying, BuildResumeAction.ParkAtGate)]
    [InlineData(BuildRunState.Recovering, BuildResumeAction.ParkAtGate)]
    // BuildResumeAction 是 App 内部类型（InternalsVisibleTo），测试方法随之声明为 internal
    internal void Evaluate_StateMapping_MatchesOriginalIfChain(BuildRunState state, BuildResumeAction expected)
    {
        // Accepting 的指纹基线是提交时指纹，夹具需对齐才能通过指纹门禁到达状态分支
        var run = state == BuildRunState.Accepting
            ? Run(state) with { CommitWorkspaceFingerprint = Fingerprint }
            : Run(state);

        BuildResumePolicy.Evaluate(run, null, Fingerprint)
            .Action.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_ClarifyingWithoutPlan_ContinuesClarification()
    {
        BuildResumePolicy.Evaluate(Run(BuildRunState.Clarifying), null, Fingerprint)
            .Action.Should().Be(BuildResumeAction.ContinueClarification);
    }

    [Fact]
    public void Evaluate_ClarifyingWithPersistedPlan_ResumesPrescribedPlan()
    {
        // 旧检查点可能在计划契约强制前进入 Clarifying；带计划的 Clarifying 直接进执行准备，
        // 不让用户对同一份计划重复批准
        var run = Run(BuildRunState.Clarifying, plan: Plan("s", "t1"));

        BuildResumePolicy.Evaluate(run, null, Fingerprint)
            .Action.Should().Be(BuildResumeAction.ResumePrescribedPlan);
    }
}
