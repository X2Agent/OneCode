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

    /// <summary>
    /// Compiled deny-list from <see cref="DangerousCommandPatterns.Layer0HardDeny"/>,
    /// aligned with BashTool/PowerShellTool safety infrastructure.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex[] DenyPatterns =
        DangerousCommandPatterns.Layer0HardDeny
            .Select(p => new System.Text.RegularExpressions.Regex(
                p.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
                TimeSpan.FromSeconds(2)))
            .ToArray();

    private static readonly string[] DenyPatternNames =
        DangerousCommandPatterns.Layer0HardDeny.Select(p => p.Name).ToArray();

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

        // 应用与 BashTool/PowerShellTool 相同的 deny-list
        for (var i = 0; i < DenyPatterns.Length; i++)
        {
            try
            {
                if (DenyPatterns[i].IsMatch(command))
                    return ToolResult.Error($"[SAFETY] Dangerous command pattern detected: '{DenyPatternNames[i]}'. Command blocked.");
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return ToolResult.Error($"[SAFETY] Command pattern check timed out (possible ReDoS). Command blocked.");
            }
        }

        cwd ??= _wd.WorkingDirectory;

        // Validate paths referenced in the command string (same as BashTool/PowerShellTool).
        var pathValidationError = ShellExecutionHelper.ValidateReferencedPaths(
            command, _wd.WorkingDirectory, BashCommandClassifier.ExtractReferencedPaths, _wd.AdditionalDirectories);
        if (pathValidationError != null)
            return ToolResult.Error(pathValidationError);

        // 通过 PathsHelper.SafeResolve 校验 cwd
        var cwdResult = PathsHelper.SafeResolve(cwd, _wd.WorkingDirectory);
        if (!cwdResult.IsSuccess)
            return ToolResult.Error($"Error: working directory '{cwd}' is outside the allowed workspace");
        cwd = cwdResult.Value;

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
            System.Diagnostics.Process? proc = null;
            try
            {
                _taskService.UpdateTask(task.Id, status: TaskStatus.InProgress);
                var isWindows = OperatingSystem.IsWindows();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = isWindows ? "cmd" : "sh",
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(isWindows ? "/c" : "-c");
                psi.ArgumentList.Add(command);

                proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) { _taskService.UpdateTask(task.Id, status: TaskStatus.Failed); return; }

                // Task 'stop' 取消 token 时杀掉整个进程树（cmd/sh 启动的子进程一并终止）
                using var killReg = taskToken.Register(() =>
                {
                    try { proc.Kill(entireProcessTree: true); }
                    catch (Exception killEx)
                    {
                        _logger.LogDebug(killEx, "Failed to kill process tree for task {TaskId}", task.Id);
                    }
                });

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(taskToken);
                var stderrTask = proc.StandardError.ReadToEndAsync(taskToken);
                await proc.WaitForExitAsync(taskToken).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                var output = stdout + stderr;
                if (!string.IsNullOrWhiteSpace(output))
                    _taskService.AppendTaskOutput(task.Id, output.TrimEnd());
                _taskService.UpdateTask(task.Id,
                    status: proc.ExitCode == 0 ? TaskStatus.Completed : TaskStatus.Failed);
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
            finally
            {
                proc?.Dispose();
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
