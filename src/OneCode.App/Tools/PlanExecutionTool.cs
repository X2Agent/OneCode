using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.Core.PlanMode;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Tools;

/// <summary>Typed tools used by the approved Build run to persist step and verification progress.</summary>
public sealed class PlanExecutionTool(
    IPlanWorkflowApplicationService workflowService,
    PlanCardPublisher publisher,
    ITaskService taskService)
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
            ReconcileLinkedBuildTasks(context.SessionId, context.Workflow);
            var linkedTask = GetLinkedBuildTask(context.SessionId, stepId);
            ValidateLinkedTaskDependencies(linkedTask, parsed.Value);
            var commandId = Guid.NewGuid().ToString("N");
            var result = await workflowService.UpdateStepAsync(new UpdatePlanStepCommand(
                commandId,
                context.SessionId,
                context.Workflow.Id,
                context.RunId,
                stepId,
                parsed.Value,
                evidence,
                error), ct).ConfigureAwait(false);
            var execution = result.Workflow.StepExecutions.Single(item =>
                string.Equals(item.StepId, stepId, StringComparison.Ordinal));
            var projectedTask = GetLinkedBuildTask(context.SessionId, stepId);
            ProjectLinkedBuildTask(
                projectedTask,
                execution,
                $"{result.Workflow.Id}:{result.Workflow.Version}:{stepId}");
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

    [Description("Move an approved plan from Executing to Verifying after every step has a terminal status and evidence.")]
    public async Task<ToolResult> CompletePlanExecutionAsync(
        [Description("Summary of implemented changes and completed steps.")] string summary,
        CancellationToken ct = default)
    {
        var context = await ResolveContextAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await workflowService.CompleteExecutionAsync(new CompletePlanExecutionCommand(
                Guid.NewGuid().ToString("N"),
                context.SessionId,
                context.Workflow.Id,
                context.RunId,
                summary), ct).ConfigureAwait(false);
            publisher.Publish(result.Workflow);
            return ToolResult.JsonSuccess(new
            {
                status = "verification_required",
                planId = result.Workflow.Id.ToString(),
                workflowVersion = result.Workflow.Version,
            });
        }
        catch (Exception ex) when (ex is PlanTransitionException or PlanValidationException)
        {
            return ToolResult.Error(ex.Message);
        }
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
                summary), ct).ConfigureAwait(false);
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

    private void ReconcileLinkedBuildTasks(SessionId sessionId, PlanWorkflow workflow)
    {
        var byId = workflow.StepExecutions.ToDictionary(
            execution => execution.StepId,
            StringComparer.Ordinal);
        var orderedIds = TopologicalStepIds(workflow);
        foreach (var stepId in orderedIds)
        {
            var execution = byId[stepId];
            var task = GetLinkedBuildTask(sessionId, execution.StepId);
            var projectionKey = $"{workflow.Id}:{workflow.Version}:{execution.StepId}";
            ProjectLinkedBuildTask(task, execution, projectionKey);
        }
    }

    private static IReadOnlyList<string> TopologicalStepIds(PlanWorkflow workflow)
    {
        var definitions = workflow.ApprovedSnapshot?.Steps.ToDictionary(
            step => step.Id,
            StringComparer.Ordinal)
            ?? throw new PlanTransitionException(
                $"Plan '{workflow.Id}' has no approved step definitions for Build task reconciliation.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(definitions.Count);

        void Visit(string id)
        {
            if (!visited.Add(id))
                return;
            if (!definitions.TryGetValue(id, out var definition))
                throw new PlanTransitionException($"Approved plan step '{id}' was not found during reconciliation.");
            foreach (var dependency in definition.DependsOn)
                Visit(dependency);
            ordered.Add(id);
        }

        foreach (var execution in workflow.StepExecutions)
            Visit(execution.StepId);
        return ordered;
    }

    private TaskItem GetLinkedBuildTask(SessionId sessionId, string stepId)
    {
        var buildRunId = OneCodeAgentRunContext.CurrentBuildRunId
            ?? throw new PlanTransitionException("Plan execution has no active BuildRun scope.");
        return taskService.ListTasks(
                conversationId: sessionId.ToString(),
                buildRunId: buildRunId,
                exactScope: true)
            .SingleOrDefault(item =>
                item.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedId) == true
                && string.Equals(mappedId, stepId, StringComparison.Ordinal))
            ?? throw new PlanTransitionException(
                $"Approved plan step '{stepId}' has no persistent Build task mapping.");
    }

    private void ValidateLinkedTaskDependencies(
        TaskItem task,
        PlanStepExecutionStatus status)
    {
        if (status is not (PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed))
            return;

        var unresolved = task.BlockedBy
            .Where(dependencyId => taskService.GetTask(dependencyId)?.Status != TaskStatus.Completed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            throw new PlanTransitionException(
                $"Approved plan step cannot advance because persistent Build task '{task.Id}' is blocked by: {string.Join(", ", unresolved)}.");
        }
    }

    private void ProjectLinkedBuildTask(
        TaskItem task,
        PlanStepExecution execution,
        string projectionKey)
    {
        var taskStatus = execution.Status switch
        {
            PlanStepExecutionStatus.Pending => TaskStatus.Pending,
            PlanStepExecutionStatus.InProgress => TaskStatus.InProgress,
            PlanStepExecutionStatus.Completed => TaskStatus.Completed,
            PlanStepExecutionStatus.Failed => TaskStatus.Failed,
            PlanStepExecutionStatus.Skipped => TaskStatus.Completed,
            PlanStepExecutionStatus.Cancelled => TaskStatus.Cancelled,
            _ => throw new PlanTransitionException($"Unsupported plan step status '{execution.Status}'."),
        };
        var output = execution.Status == PlanStepExecutionStatus.Failed
            ? execution.Error
            : execution.Evidence;
        var projected = taskService.ProjectTaskStatus(
            task.Id,
            taskStatus,
            output,
            projectionKey,
            requireCompletedDependencies: execution.Status is
                PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed);
        if (!projected.Succeeded)
        {
            throw new PlanTransitionException(
                projected.Error
                ?? $"Persistent Build task '{task.Id}' could not project plan step state '{execution.Status}'.");
        }
    }

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
