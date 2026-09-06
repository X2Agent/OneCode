using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.PlanMode;
using OneCode.Core.PlanMode;

namespace OneCode.App.Tools;

/// <summary>Typed tools used by the approved Build run to persist step and verification progress.</summary>
public sealed class PlanExecutionTool(
    IPlanWorkflowApplicationService workflowService,
    PlanCardPublisher publisher,
    BuildTaskLinker taskLinker)
{
    [Description("Persist an approved-plan step status. Completed steps require concrete evidence; failed steps require an error.")]
    public async Task<ToolResult> UpdatePlanStepAsync(
        [Description("Structured step ID from the approved snapshot.")] string stepId,
        [Description("One of: in_progress, completed, failed, skipped.")] string status,
        [Description("Concrete evidence for completed/skipped steps, such as changed files or command output.")] string? evidence = null,
        [Description("Failure detail when status is failed.")] string? error = null,
        CancellationToken ct = default)
    {
        var context = await ResolveContextAsync(ct).ConfigureAwait(false);
        var parsed = status.ToLowerInvariant() switch
        {
            "in_progress" => PlanStepExecutionStatus.InProgress,
            "completed" => PlanStepExecutionStatus.Completed,
            "failed" => PlanStepExecutionStatus.Failed,
            "skipped" => PlanStepExecutionStatus.Skipped,
            _ => (PlanStepExecutionStatus?)null,
        };
        if (parsed is null)
            return ToolResult.Error("status must be one of: in_progress, completed, failed, skipped");

        try
        {
            var buildRunScope = ResolveBuildRunScope();
            taskLinker.ReconcilePlanStepProjections(context.SessionId.ToString(), buildRunScope, context.Workflow);
            var linkedTask = taskLinker.GetLinkedPlanTask(context.SessionId.ToString(), buildRunScope, stepId);
            taskLinker.ValidatePlanStepDependencies(linkedTask, parsed.Value);
            var commandId = Guid.NewGuid().ToString("N");
            var result = await workflowService.UpdateStepAsync(new UpdatePlanStepCommand(
                commandId,
                context.SessionId,
                context.Workflow.Id,
                context.RunId,
                stepId,
                parsed.Value,
                evidence,
                error,
                context.Workflow.WorkflowFencingToken ?? 0), ct).ConfigureAwait(false);
            var execution = result.Workflow.StepExecutions.SingleOrDefault(item =>
                string.Equals(item.StepId, stepId, StringComparison.Ordinal));
            if (execution is null)
                return ToolResult.Error($"Step '{stepId}' not found in workflow {result.Workflow.Id}. " +
                    $"Known steps: {string.Join(", ", result.Workflow.StepExecutions.Select(s => s.StepId))}");
            var projectedTask = taskLinker.GetLinkedPlanTask(context.SessionId.ToString(), buildRunScope, stepId);
            taskLinker.ProjectPlanStep(
                projectedTask,
                execution,
                $"{result.Workflow.Id}:{result.Workflow.Version}:{stepId}");

            // CompletePlanExecution 已收敛为编排层自动推导：所有 step 达到终态
            // （且 Completed 均有 evidence）时自动 Executing → Verifying。
            if (result.Workflow.State == PlanWorkflowState.Executing && IsAllStepsTerminal(result.Workflow))
            {
                var completion = await workflowService.CompleteExecutionAsync(new CompletePlanExecutionCommand(
                    Guid.NewGuid().ToString("N"),
                    context.SessionId,
                    result.Workflow.Id,
                    context.RunId,
                    BuildExecutionSummary(result.Workflow),
                    context.Workflow.WorkflowFencingToken ?? 0), ct).ConfigureAwait(false);
                publisher.Publish(completion.Workflow);
                return ToolResult.JsonSuccess(new
                {
                    status = "verification_required",
                    planId = completion.Workflow.Id.ToString(),
                    stepId,
                    stepStatus = parsed.Value.ToString(),
                    workflowVersion = completion.Workflow.Version,
                });
            }

            publisher.Publish(result.Workflow);
            return ToolResult.JsonSuccess(new
            {
                status = "step_updated",
                planId = result.Workflow.Id.ToString(),
                stepId,
                stepStatus = parsed.Value.ToString(),
                workflowVersion = result.Workflow.Version,
            });
        }
        catch (Exception ex) when (ex is PlanTransitionException or PlanValidationException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private static bool IsAllStepsTerminal(PlanWorkflow workflow) =>
        workflow.StepExecutions.Count > 0
        && workflow.StepExecutions.All(step =>
            step.Status is PlanStepExecutionStatus.Completed or PlanStepExecutionStatus.Skipped
            && (step.Status != PlanStepExecutionStatus.Completed || !string.IsNullOrWhiteSpace(step.Evidence)));

    /// <summary>从各 step 证据拼出执行摘要（自动推导 CompleteExecution 时替代 LLM 提供的 summary）。</summary>
    private static string BuildExecutionSummary(PlanWorkflow workflow)
    {
        var lines = workflow.StepExecutions
            .Where(step => step.Status == PlanStepExecutionStatus.Completed && !string.IsNullOrWhiteSpace(step.Evidence))
            .Select(step => $"{step.StepId}: {step.Evidence}");
        var summary = string.Join("\n", lines);
        return summary.Length <= 2000 ? summary : summary[..2000];
    }

    [Description("Finish plan verification. A passing result requires concrete build/test/check evidence.")]
    public async Task<ToolResult> CompletePlanVerificationAsync(
        [Description("Whether all required verification gates passed.")] bool passed,
        [Description("Concrete command results or artifact checks proving verification.")] string[] evidence,
        [Description("Final verification and delivery summary.")] string summary,
        CancellationToken ct = default)
    {
        var context = await ResolveContextAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await workflowService.CompleteVerificationAsync(new CompletePlanVerificationCommand(
                Guid.NewGuid().ToString("N"),
                context.SessionId,
                context.Workflow.Id,
                context.RunId,
                passed,
                evidence,
                summary,
                context.Workflow.WorkflowFencingToken ?? 0), ct).ConfigureAwait(false);
            publisher.Publish(result.Workflow);
            return ToolResult.JsonSuccess(new
            {
                status = result.Workflow.State.ToString(),
                planId = result.Workflow.Id.ToString(),
                workflowVersion = result.Workflow.Version,
            });
        }
        catch (Exception ex) when (ex is PlanTransitionException or PlanValidationException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>Plan 执行的持久化任务作用域：当前 BuildRun（ambient 上下文，缺失即 fail-closed）。</summary>
    private static string ResolveBuildRunScope()
        => OneCodeAgentRunContext.CurrentBuildRunId
           ?? throw new PlanTransitionException("Plan execution has no active BuildRun scope.");

    private async Task<(SessionId SessionId, PlanWorkflow Workflow, string RunId)> ResolveContextAsync(
        CancellationToken ct)
    {
        var sessionId = SessionId.TryParse(ToolActivationContext.CurrentConversationId)
            ?? throw new PlanTransitionException("Plan execution requires an explicit agent run conversation.");
        var runId = OneCodeAgentRunContext.CurrentRunId
            ?? throw new PlanTransitionException("Plan execution tool can only run inside an agent run.");
        var workflow = await workflowService.GetAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new PlanTransitionException("No plan workflow exists for the active conversation.");
        if (workflow.State is not (PlanWorkflowState.Executing or PlanWorkflowState.Verifying))
            throw new PlanTransitionException($"Plan execution tool is unavailable in state '{workflow.State}'.");
        if (!string.Equals(workflow.ActiveRunId, runId, StringComparison.Ordinal))
            throw new PlanTransitionException($"Run '{runId}' does not own plan '{workflow.Id}'.");
        return (sessionId, workflow, runId);
    }
}
