using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;
using System.Text;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.Context;

/// <summary>
/// Injects the current task list into the LLM context before each turn.
///
/// <para>
/// Replaces MAF's <c>TodoProvider</c> — which provided a simple todo list with
/// <c>todo_create/get/update/delete/list</c> tools — with a read-only context
/// provider backed by OneCode's richer <see cref="ITaskService"/> (dependency tracking,
/// status enum, owner, output logs). The LLM sees the current task state automatically
/// without needing to call the Task tool with action 'list' explicitly.
/// </para>
///
/// <para>
/// Only non-completed tasks are injected to avoid wasting context budget.
/// The unified Task tool (action: create/update/get/list/stop/output) remains the mutation API;
/// this provider is read-only.
/// </para>
/// </summary>
public sealed class TaskContextProvider : ReadOnlyAIContextProviderBase
{
    private readonly ITaskService _taskService;
    private readonly ILogger<TaskContextProvider>? _logger;

    public TaskContextProvider(ITaskService taskService, ILogger<TaskContextProvider>? logger = null)
    {
        _taskService = taskService;
        _logger = logger;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var convId = ToolActivationContext.CurrentConversationId;
        var buildRunId = OneCodeAgentRunContext.CurrentBuildRunId;
        var activeTasks = _taskService.ListTasks(
                conversationId: convId,
                buildRunId: buildRunId,
                exactScope: true)
            .Where(t => t.Status is TaskStatus.Pending or TaskStatus.InProgress)
            .ToList();

        if (activeTasks.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        var sb = new StringBuilder();
        sb.AppendLine("## Current Task List");
        sb.AppendLine();
        sb.AppendLine("The following tasks are in progress or pending. Use the Task tool with action 'update' to mark them complete when done.");
        sb.AppendLine();

        var completedIds = new HashSet<string>(
            _taskService.ListTasks(
                    conversationId: convId,
                    buildRunId: buildRunId,
                    exactScope: true)
                .Where(t => t.Status == TaskStatus.Completed)
                .Select(t => t.Id));

        foreach (var task in activeTasks)
        {
            var statusIcon = task.Status == TaskStatus.InProgress ? "🔄" : "⏳";
            var owner = !string.IsNullOrEmpty(task.Owner) ? $" ({task.Owner})" : "";

            var unresolvedBlockers = task.BlockedBy
                .Where(b => !completedIds.Contains(b))
                .ToList();

            var blockerHint = unresolvedBlockers.Count > 0
                ? $" [blocked by: {string.Join(", ", unresolvedBlockers.Select(b => $"#{b}"))}]"
                : "";

            sb.AppendLine(CultureInfo.InvariantCulture, $"- {statusIcon} #{task.Id}: {task.Subject}{owner}{blockerHint}");

            if (!string.IsNullOrEmpty(task.ActiveForm))
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Status: {task.ActiveForm}");
        }

        _logger?.LogDebug("Injected {Count} active tasks into LLM context", activeTasks.Count);

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, sb.ToString())],
        });
    }
}
