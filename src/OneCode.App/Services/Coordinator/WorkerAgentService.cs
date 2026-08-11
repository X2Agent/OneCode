using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Worker agent lifecycle manager with TaskService integration.
///
/// Wraps execution with per-worker task tracking:
///   1. Creates a TaskService entry when the worker starts.
///   2. Delegates to <see cref="ForkedAgentRunner"/>.
///   3. Updates task status to Completed/Failed/Cancelled on finish.
///   4. Appends agent output to the task log for Task tool (output action) visibility.
///
/// Registered as the IAgentRunner singleton so AgentTool automatically
/// gets task tracking for every sub-agent invocation.
/// Injects <see cref="ForkedAgentRunner"/> directly (not <see cref="IAgentRunner"/>)
/// to avoid a DI cycle with the WorkerAgentService → IAgentRunner registration.
/// </summary>
public sealed class WorkerAgentService(
    ForkedAgentRunner agentRunner,
    ITaskService taskService,
    ILogger<WorkerAgentService> logger) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken ct = default)
    {
        TaskItem task;
        if (request.TaskId is not null && taskService.GetTask(request.TaskId) is { } existing)
        {
            // 调用方已预建任务（AgentTool background）——更新而非新建，避免重复条目。
            task = existing;
        }
        else
        {
            var subject = request.Description ?? request.Agent;
            var descPreview = request.Prompt.Length > 120
                ? string.Concat(request.Prompt.AsSpan(0, 120), "...")
                : request.Prompt;

            task = taskService.CreateTask(
                subject: subject,
                description: descPreview,
                activeForm: $"Running {request.Agent}",
                status: TaskStatus.InProgress,
                owner: request.Agent,
                conversationId: ToolActivationContext.CurrentConversationId,
                buildRunId: OneCodeAgentRunContext.CurrentBuildRunId);
        }

        logger.LogDebug(
            "Worker started: task={TaskId} agent={Agent}",
            task.Id, request.Agent);

        try
        {
            var result = await agentRunner.RunAsync(request, ct).ConfigureAwait(false);

            // ERR-1.4: 子 Agent 返回结构化错误时，标记任务为 Failed 并写入错误详情，
            // 而非误标为 Completed。返回的 AgentRunResult 已携带 Error 字段，供上游消费方透传。
            if (result.Error is not null)
            {
                taskService.UpdateTask(task.Id, status: TaskStatus.Failed, activeForm: null);
                taskService.AppendTaskOutput(task.Id,
                    $"Error: [{result.Error.Type}] {result.Error.Detail}");

                logger.LogWarning(
                    "Worker returned problem details: task={TaskId} type={Type} traceId={TraceId}",
                    task.Id, result.Error.Type, result.Error.TraceId ?? "(none)");
            }
            else
            {
                taskService.UpdateTask(task.Id, status: TaskStatus.Completed, activeForm: null);
                if (!string.IsNullOrEmpty(result.Output))
                    taskService.AppendTaskOutput(task.Id, result.Output);

                logger.LogDebug(
                    "Worker completed: task={TaskId} turns={Turns} maxTurns={Max}",
                    task.Id, result.TurnsCompleted, result.MaxTurnsReached);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            taskService.UpdateTask(task.Id, status: TaskStatus.Cancelled, activeForm: null);
            logger.LogDebug("Worker cancelled: task={TaskId}", task.Id);
            throw;
        }
        catch (Exception ex)
        {
            taskService.UpdateTask(task.Id, status: TaskStatus.Failed, activeForm: null);
            taskService.AppendTaskOutput(task.Id, $"Error: {ex.Message}");
            logger.LogWarning(ex, "Worker failed: task={TaskId}", task.Id);
            throw;
        }
    }

    public async Task<AgentRunResult> RunWorkerAsync(
        string prompt,
        string agentName,
        string? workingDirectory = null,
        CancellationToken ct = default)
        => await RunAsync(new AgentRunRequest(
            Prompt: prompt,
            Agent: agentName,
            Description: agentName), ct).ConfigureAwait(false);
}
