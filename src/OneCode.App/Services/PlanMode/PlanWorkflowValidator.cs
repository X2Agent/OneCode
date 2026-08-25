using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// PlanWorkflow 状态机命令校验的纯函数集：命令身份、活动运行绑定、步骤转换合法性、乐观并发版本。
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

    public static void ValidateActiveBuildRun(
        PlanWorkflow workflow,
        string runId,
        PlanWorkflowState expectedState)
    {
        if (workflow.State != expectedState)
            throw InvalidState(workflow, expectedState);
        if (!string.Equals(workflow.ActiveRunId, runId, StringComparison.Ordinal))
            throw new PlanTransitionException(
                $"Run '{runId}' does not match active run '{workflow.ActiveRunId}'.");
    }

    public static void ValidateStepTransition(
        PlanWorkflow workflow,
        PlanStepExecution currentStep,
        UpdatePlanStepCommand command)
    {
        if (command.Status == PlanStepExecutionStatus.Completed && string.IsNullOrWhiteSpace(command.Evidence))
            throw new PlanValidationException($"Completed step '{command.StepId}' requires evidence.");
        if (command.Status == PlanStepExecutionStatus.Failed && string.IsNullOrWhiteSpace(command.Error))
            throw new PlanValidationException($"Failed step '{command.StepId}' requires an error.");
        if (currentStep.Status is PlanStepExecutionStatus.Completed
            or PlanStepExecutionStatus.Failed
            or PlanStepExecutionStatus.Skipped
            or PlanStepExecutionStatus.Cancelled)
        {
            if (currentStep.Status != command.Status)
            {
                throw new PlanTransitionException(
                    $"Plan step '{command.StepId}' is terminal in state '{currentStep.Status}' and cannot transition to '{command.Status}'.");
            }

            return;
        }
        if (currentStep.Status == PlanStepExecutionStatus.Pending
            && command.Status is not (PlanStepExecutionStatus.InProgress
                or PlanStepExecutionStatus.Failed
                or PlanStepExecutionStatus.Skipped))
        {
            throw new PlanTransitionException(
                $"Plan step '{command.StepId}' cannot transition directly from Pending to '{command.Status}'.");
        }

        if (command.Status is not (PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed))
            return;

        var definition = workflow.ApprovedSnapshot?.Steps.SingleOrDefault(step =>
            string.Equals(step.Id, command.StepId, StringComparison.Ordinal))
            ?? throw new PlanTransitionException(
                $"Approved plan definition for step '{command.StepId}' was not found.");
        var unresolved = definition.DependsOn
            .Where(dependencyId => workflow.StepExecutions.SingleOrDefault(step =>
                    string.Equals(step.StepId, dependencyId, StringComparison.Ordinal))?.Status
                != PlanStepExecutionStatus.Completed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            throw new PlanTransitionException(
                $"Plan step '{command.StepId}' is blocked by incomplete dependencies: {string.Join(", ", unresolved)}.");
        }
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
