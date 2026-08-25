using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>BeginOrResume 对非终态 BuildRun 的恢复动作裁决。</summary>
internal enum BuildResumeAction
{
    /// <summary>调用方指定计划与持久化计划冲突——调用方抛 InvalidOperationException。</summary>
    PlanConflict,

    /// <summary>工作区指纹漂移——转入 Blocked（Accepting 与检查点两种提示语）。</summary>
    FingerprintDrift,

    /// <summary>Accepting 态恢复——补完被中断的提交确认。</summary>
    ConfirmCommit,

    /// <summary>Intake/Assessing——继续需求评估。</summary>
    ContinueAssessment,

    /// <summary>Clarifying 但已有持久化计划——计划即已批准的契约，直接进入执行准备。</summary>
    ResumePrescribedPlan,

    /// <summary>Clarifying 无计划——继续澄清对话。</summary>
    ContinueClarification,

    /// <summary>ScopeConfirmed/Planning——按已确认范围准备执行。</summary>
    PrepareFromScope,

    /// <summary>审批门/执行中态——原样停靠并通知观察者。</summary>
    ParkAtGate,
}

/// <param name="Action">裁决动作。</param>
/// <param name="FailureSummary">FingerprintDrift 时的阻断原因文案。</param>
internal sealed record BuildResumeDecision(BuildResumeAction Action, string? FailureSummary = null);

/// <summary>
/// 纯函数裁决器：非终态 BuildRun 在 BeginOrResumeAsync 中的恢复路径。
/// 输入已加载的 run、调用方指定的计划与当前工作区指纹，输出动作；
/// 不做 I/O、不改状态、不抛异常（PlanConflict 由调用方转译为异常）。
/// 状态→动作的映射规则集中于此，取代原先散落在 BeginOrResumeAsync 里的 if 链。
/// </summary>
internal static class BuildResumePolicy
{
    public static BuildResumeDecision Evaluate(
        BuildRun existing,
        BuildPlan? prescribedPlan,
        string currentFingerprint)
    {
        if (prescribedPlan is not null
            && existing.Plan is not null
            && !BuildRunCoordinator.PlansMatch(existing.Plan, prescribedPlan))
        {
            return new BuildResumeDecision(BuildResumeAction.PlanConflict);
        }

        // Accepting 态以提交时指纹为基线（提交后工作区允许变化），其余状态以检查点指纹为基线。
        var expectedFingerprint = existing.State == BuildRunState.Accepting
            ? existing.CommitWorkspaceFingerprint
            : existing.WorkspaceFingerprint;
        if (!string.Equals(expectedFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            var summary = existing.State == BuildRunState.Accepting
                ? "Workspace changed after final validation; commit recovery requires manual reconciliation."
                : "Workspace changed after the BuildRun checkpoint; re-baselining is required before writes can resume.";
            return new BuildResumeDecision(BuildResumeAction.FingerprintDrift, summary);
        }

        return existing.State switch
        {
            BuildRunState.Accepting => new(BuildResumeAction.ConfirmCommit),
            BuildRunState.Intake or BuildRunState.Assessing => new(BuildResumeAction.ContinueAssessment),

            // 持久化计划即已批准的范围契约。旧检查点可能在强制前进入 Clarifying；
            // 直接恢复而不是让用户对同一份计划重复批准。
            BuildRunState.Clarifying when existing.Plan is not null
                => new(BuildResumeAction.ResumePrescribedPlan),
            BuildRunState.Clarifying => new(BuildResumeAction.ContinueClarification),
            BuildRunState.ScopeConfirmed or BuildRunState.Planning
                => new(BuildResumeAction.PrepareFromScope),

            // Planned 是用户审批门：计划停靠在此直到 Approve/Reject。
            // Implementing/Verifying/Recovering 及其余非终态一律原样停靠。
            _ => new(BuildResumeAction.ParkAtGate),
        };
    }
}
