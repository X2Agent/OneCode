using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Tools;

/// <summary>
/// Unified Task tool — manages background tasks via action-based routing.
/// Replaces the former TaskCreate/TaskUpdate/TaskList/TaskGet/TaskStop/TaskOutput tools.
/// </summary>
public sealed class TaskTool
{
    private readonly ITaskService _taskService;

    public TaskTool(ITaskService taskService) => _taskService = taskService;

    [Description("Manage background tasks: create, update, get, list, stop, or get output. " +
                 "Use 'list' to see all tasks, 'get' for details, 'create' to make a new task, " +
                 "'update' to change status/subject, 'stop' to cancel, 'output' to read task output.")]
    public Task<ToolResult> ExecuteAsync(
        [Description("Action: create, update, get, list, stop, output.")] string action,
        [Description("Task ID (required for get/update/stop/output, ignored for list/create).")] string? taskId = null,
        [Description("Task subject/title (for create or update).")] string? subject = null,
        [Description("Task description (for create or update).")] string? description = null,
        [Description("New status for update: pending, in_progress, completed, failed, cancelled.")] string? status = null,
        [Description("Present continuous form for spinner display, e.g. 'Running tests' (for create or update).")] string? activeForm = null,
        [Description("Max lines to return (for output action).")] int? maxLines = null,
        CancellationToken ct = default)
    {
        return action.ToLowerInvariant() switch
        {
            "create" => CreateAsync(subject, description, activeForm),
            "update" => UpdateAsync(taskId, subject, description, status, activeForm),
            "get" => Task.FromResult(Get(taskId)),
            "list" => Task.FromResult(List()),
            "stop" => Task.FromResult(Stop(taskId)),
            "output" => Task.FromResult(GetOutput(taskId, maxLines)),
            _ => Task.FromResult(ToolResult.Error(
                $"Unknown action '{action}'. Valid: create, update, get, list, stop, output.")),
        };
    }

    private Task<ToolResult> CreateAsync(string? subject, string? description, string? activeForm)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return Task.FromResult(ToolResult.Error("subject is required for create action"));

        var convId = ToolActivationContext.CurrentConversationId;
        var task = _taskService.CreateTask(
            subject,
            description ?? "",
            activeForm,
            conversationId: convId,
            buildRunId: OneCodeAgentRunContext.CurrentBuildRunId);
        return Task.FromResult(ToolResult.JsonSuccess(
            new { task = new { id = task.Id, subject = task.Subject } }));
    }

    private Task<ToolResult> UpdateAsync(
        string? taskId, string? subject, string? description, string? status, string? activeForm)
    {
        if (string.IsNullOrEmpty(taskId))
            return Task.FromResult(ToolResult.Error("taskId is required for update action"));

        TaskStatus? statusEnum = status?.ToLowerInvariant() switch
        {
            "pending" => TaskStatus.Pending,
            "in_progress" => TaskStatus.InProgress,
            "completed" => TaskStatus.Completed,
            "failed" => TaskStatus.Failed,
            "cancelled" => TaskStatus.Cancelled,
            _ => null,
        };

        if (!TryGetScopedTask(taskId, out _))
            return Task.FromResult(ToolResult.Error($"Task #{taskId} not found"));

        var updated = _taskService.UpdateTask(taskId, subject, description, statusEnum, activeForm);
        return Task.FromResult(updated
            ? ToolResult.JsonSuccess(
                new { task = new { id = taskId, status = statusEnum?.ToString() ?? "updated" } })
            : ToolResult.Error($"Task #{taskId} not found"));
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
