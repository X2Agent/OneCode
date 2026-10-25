using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;
using OneCode.Infrastructure;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Tools;

/// <summary>BackgroundRun tool — runs a command in the background as a trackable task.</summary>
public sealed class BackgroundRunTool
{
    private readonly ITaskService _taskService;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILogger<BackgroundRunTool> _logger;

    public BackgroundRunTool(ITaskService taskService, IWorkingDirectoryAccessor wd, ILogger<BackgroundRunTool> logger)
    {
        _taskService = taskService; _wd = wd; _logger = logger;
    }

    [Description("Run a shell command in the background. Use BackgroundWait to wait for completion.")]
    public async Task<ToolResult> RunAsync(
        [Description("The command to run.")] string command,
        [Description("What this command does (for task tracking).")] string description,
        [Description("Working directory (default: current).")] string? cwd = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("Error: command cannot be empty");

        if (string.IsNullOrWhiteSpace(description))
            return ToolResult.Error("Error: description is required for task tracking");

        var safetyCheck = OneCode.Infrastructure.Middleware.Invariants.BashCommandInvariant.CheckCommand(command);
        if (!safetyCheck.Allowed)
            return ToolResult.Error(safetyCheck.Reason!);

        cwd ??= _wd.WorkingDirectory;

        // Validate paths referenced in the command string (same as BashTool).
        var pathValidationError = ShellExecutionHelper.ValidateReferencedPaths(
            command, _wd.WorkingDirectory, BashCommandClassifier.ExtractReferencedPaths, _wd.AdditionalDirectories);
        if (pathValidationError is not null)
            return ToolResult.Error(pathValidationError);

        // 通过 PathsHelper.SafeResolve 校验 cwd
        var cwdResult = PathsHelper.SafeResolve(cwd, _wd.WorkingDirectory);
        if (!cwdResult.IsSuccess)
            return ToolResult.Error($"Error: working directory '{cwd}' is outside the allowed workspace");
        // IsSuccess 已保证 Value 非空
        var resolvedCwd = cwdResult.Value!;

        var task = _taskService.CreateTask(
            command,
            description,
            $"Running: {command}",
            conversationId: ToolActivationContext.CurrentConversationId,
            buildRunId: OneCodeAgentRunContext.CurrentBuildRunId);
        var taskToken = _taskService.GetTaskToken(task.Id);

        // Use a tracked background task instead of fire-and-forget
        var bgTask = Task.Run(async () =>
        {
            try
            {
                _taskService.UpdateTask(task.Id, status: TaskStatus.InProgress);
                var (exitCode, stdout, stderr) = await BackgroundShellProcess.RunAsync(command, resolvedCwd, taskToken, _logger).ConfigureAwait(false);

                var output = stdout + stderr;
                if (!string.IsNullOrWhiteSpace(output))
                    _taskService.AppendTaskOutput(task.Id, output.TrimEnd());
                _taskService.UpdateTask(task.Id,
                    status: exitCode == 0 ? TaskStatus.Completed : TaskStatus.Failed);
            }
            catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
            {
                _taskService.AppendTaskOutput(task.Id, "(cancelled via Task stop)");
                _taskService.UpdateTask(task.Id, status: TaskStatus.Cancelled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background task {TaskId} failed", task.Id);
                _taskService.AppendTaskOutput(task.Id, $"Error: {ex.Message}");
                _taskService.UpdateTask(task.Id, status: TaskStatus.Failed);
            }
        }, CancellationToken.None);

        // Log unobserved exceptions to prevent silent failures.
        // Intentional fire-and-forget on the continuation — the primary task (bgTask) is the
        // one being monitored; the continuation just ensures exceptions surface in logs.
#pragma warning disable CS4014
        bgTask.ContinueWith(
            t => _logger.LogError(t.Exception, "Unobserved exception in background task {TaskId}", task.Id),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
#pragma warning restore CS4014

        return ToolResult.JsonSuccess(new
        {
            taskId = task.Id,
            status = "started",
            command,
            message = "Background task started. Use the Task tool with action 'get' or 'output' to check status."
        });
    }
}
