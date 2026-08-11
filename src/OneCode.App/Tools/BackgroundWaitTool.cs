using System.ComponentModel;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Tools;

/// <summary>BackgroundWait tool — waits for a background task to complete.</summary>
public sealed class BackgroundWaitTool
{
    private readonly ITaskService _taskService;

    public BackgroundWaitTool(ITaskService taskService) => _taskService = taskService;

    [Description("Wait for a background task to complete. Polls every 500ms. " +
                 "IMPORTANT: always inspect the 'status' field of the result — on timeout it returns " +
                 "status='timeout' (the task keeps running in the background), NOT an error. " +
                 "Terminal statuses are 'Completed'/'Failed'/'Cancelled'.")]
    public async Task<ToolResult> WaitAsync(
        [Description("Task ID to wait for.")] string taskId,
        [Description("Max wait time in seconds (default 60).")] int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var task = _taskService.GetTask(taskId);
            if (task == null) return ToolResult.Error($"Task #{taskId} not found");
            if (task.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
            {
                var output = _taskService.GetTaskOutput(taskId);
                return ToolResult.JsonSuccess(new { taskId, status = task.Status.ToString(), output });
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return ToolResult.JsonSuccess(new { taskId, status = "timeout", message = $"Task did not complete within {timeoutSeconds}s" });
    }
}
