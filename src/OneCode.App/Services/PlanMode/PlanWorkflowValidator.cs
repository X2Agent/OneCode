using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// PlanWorkflow 状态机命令校验的纯函数集：命令身份、乐观并发版本、状态守卫。
/// 全部为静态方法，违规即抛 PlanTransitionException / PlanValidationException / PlanConcurrencyException；
/// 转换规则的回归由此处的测试锚定。
/// </summary>
internal static class PlanWorkflowValidator
{
    public static void ValidateCommandIdentity(
        PlanWorkflow workflow,
        PlanWorkflowId planId,
        int revision,
        long expectedVersion)
    {
        if (workflow.Id != planId)
            throw new PlanTransitionException("Plan ID does not match the active workflow.");
        if (workflow.SubmittedRevision != revision)
            throw new PlanTransitionException(
                $"Revision {revision} is not the submitted revision {workflow.SubmittedRevision}.");
        if (workflow.Version != expectedVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {expectedVersion}, actual {workflow.Version}.");
    }

    public static void ValidateExpectedVersion(PlanWorkflow? workflow, long expectedVersion)
    {
        var actualVersion = workflow?.Version ?? -1;
        if (actualVersion != expectedVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {expectedVersion}, actual {actualVersion}.");
    }

    public static PlanTransitionException InvalidState(PlanWorkflow workflow, PlanWorkflowState expected)
        => new($"Plan workflow '{workflow.Id}' is in state '{workflow.State}', expected '{expected}'.");
}
