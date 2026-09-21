using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Tools;

/// <summary>
/// Host execution management for background tasks.
///
/// <para><b>Not the agent's checklist.</b> The agent's own todo list is the Harness <c>todos_*</c>
/// tool set (per-session, owned by the model). This tool exposes the <b>host</b> view: records created
/// by background execution (<c>BackgroundRun</c>, <c>Agent</c>), worker calls and Build tasks. Those
/// records carry output, cancellation and dependency state that a checklist cannot express, so they
/// stay on the product task service.</para>
///
/// <para>The former <c>create</c> / <c>update</c> actions are gone: they only ever wrote the ordinary
/// checklist, which <c>todos_*</c> now owns. Keeping both would leave two competing checklists in the
/// same context.</para>
/// </summary>
public sealed class TaskTool
{
    private readonly ITaskService _taskService;

    public TaskTool(ITaskService taskService) => _taskService = taskService;

    [Description("Inspect and control host-managed background tasks. " +
                 "'list' shows active and recent tasks, 'get' shows one task's details, " +
                 "'stop' cancels a running task, 'output' reads a task's output. " +
                 "For your own step-by-step plan use the todo tools instead.")]
    public Task<ToolResult> ExecuteAsync(
        [Description("Action: get, list, stop, output.")] string action,
        [Description("Task ID (required for get/stop/output, ignored for list).")] string? taskId = null,
        [Description("Max lines to return (for output action).")] int? maxLines = null,
        CancellationToken ct = default)
    {
        return action.ToLowerInvariant() switch
        {
            "get" => Task.FromResult(Get(taskId)),
            "list" => Task.FromResult(List()),
            "stop" => Task.FromResult(Stop(taskId)),
            "output" => Task.FromResult(GetOutput(taskId, maxLines)),
            _ => Task.FromResult(ToolResult.Error(
                $"Unknown action '{action}'. Valid: get, list, stop, output. " +
                "For your own checklist use the todo tools.")),
        };
    }

    private ToolResult Get(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
            return ToolResult.Error("taskId is required for get action");

        if (!TryGetScopedTask(taskId, out var task))
            return ToolResult.Error($"Task #{taskId} not found");

        return ToolResult.JsonSuccess(new
        {
            task.Id,
            task.Subject,
            task.Description,
            task.ActiveForm,
            Status = task.Status.ToString(),
            task.Owner,
            task.Blocks,
            task.BlockedBy,
            task.CreatedAt,
            task.UpdatedAt,
        });
    }

    private ToolResult List()
    {
        var convId = ToolActivationContext.CurrentConversationId;
        var taskList = _taskService.FormatTaskList(
            convId,
            OneCodeAgentRunContext.CurrentBuildRunId,
            exactScope: true);
        return ToolResult.Success(taskList);
    }

    private ToolResult Stop(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
            return ToolResult.Error("taskId is required for stop action");

        if (!TryGetScopedTask(taskId, out var task))
            return ToolResult.Error($"Task #{taskId} not found");

        if (task.Status is TaskStatus.Completed or TaskStatus.Cancelled or TaskStatus.Failed)
            return ToolResult.Error($"Task #{taskId} is already {task.Status}");

        var cancelled = _taskService.UpdateTask(taskId, status: TaskStatus.Cancelled);
        return cancelled
            ? ToolResult.JsonSuccess(
                new { task = new { id = taskId, status = "cancelled" } })
            : ToolResult.Error($"Task #{taskId} could not be cancelled (it may have been removed)");
    }

    private ToolResult GetOutput(string? taskId, int? maxLines)
    {
        if (string.IsNullOrEmpty(taskId))
            return ToolResult.Error("taskId is required for output action");

        if (!TryGetScopedTask(taskId, out var task))
            return ToolResult.Error($"Task #{taskId} not found");

        var output = _taskService.GetTaskOutput(taskId, maxLines);
        return ToolResult.JsonSuccess(new { taskId, output });
    }

    private bool TryGetScopedTask(string taskId, out TaskItem task)
    {
        var found = _taskService.GetTask(taskId);
        if (found is null)
        {
            task = null!;
            return false;
        }

        var conversationId = ToolActivationContext.CurrentConversationId;
        var buildRunId = OneCodeAgentRunContext.CurrentBuildRunId;
        if (!string.Equals(found.ConversationId, conversationId, StringComparison.Ordinal)
            || !string.Equals(found.BuildRunId, buildRunId, StringComparison.Ordinal))
        {
            task = null!;
            return false;
        }

        task = found;
        return true;
    }
}
