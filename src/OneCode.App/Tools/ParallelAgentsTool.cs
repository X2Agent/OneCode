using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;

namespace OneCode.App.Tools;

/// <summary>
/// Compiles and runs a typed agent task graph through the shared MAF workflow runtime.
/// </summary>
public sealed class ParallelAgentsTool(
    AgentTaskWorkflowCompiler compiler,
    AgentTaskWorkflowHost host,
    ICacheSafeParamsProvider? cacheSafeParams = null)
{

    [Description("Run multiple sub-agents in parallel with optional DAG task dependencies. Tasks without depends_on run in parallel; tasks with depends_on wait for their dependencies to complete.")]
    public async Task<ToolResult> RunParallelAsync(
        [Description("Agent tasks to run. Each task has id, prompt, agent type, optional depends_on for dependencies.")] AgentWorkflowTaskInput[]? tasks = null,
        [Description("Prepend upstream task outputs as context into dependent task prompts (default true)")] bool injectUpstreamResults = true,
        CancellationToken ct = default)
    {
        if (tasks is null || tasks.Length == 0)
            return ToolResult.Error("tasks array is required and must not be empty.");

        var workflowTasks = new List<AgentWorkflowTask>(tasks.Length);
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            if (string.IsNullOrWhiteSpace(task.Prompt))
                return ToolResult.Error("Each task must have a non-empty 'prompt'.");

            workflowTasks.Add(new AgentWorkflowTask(
                task.Id ?? $"task_{index + 1}",
                task.Prompt,
                task.Agent ?? "general-purpose",
                task.Description,
                task.DependsOn ?? [],
                injectUpstreamResults,
                task.ExecutionAccess));
        }

        try
        {
            var workflow = compiler.Compile(
                workflowTasks,
                cacheSafeParams?.Current,
                ToolActivationContext.CurrentCapabilities);
            var result = await host.RunAsync(workflow, workflowTasks, ct).ConfigureAwait(false);

            return ToolResult.JsonSuccess(new
            {
                totalTasks = result.TaskOutcomes.Count,
                allSucceeded = result.AllSucceeded,
                totalDurationMs = result.TotalDurationMs,
                totalTurnsCompleted = result.TotalTurnsCompleted,
                finalOutput = result.FinalOutput,
                tasks = result.TaskOutcomes.Select(outcome => new
                {
                    taskId = outcome.TaskId,
                    description = outcome.Description,
                    status = outcome.Status.ToString(),
                    success = outcome.Success,
                    output = outcome.Output,
                    error = outcome.Error,
                    turnsCompleted = outcome.TurnsCompleted,
                    maxTurnsReached = outcome.MaxTurnsReached,
                    durationMs = outcome.DurationMs,
                }).ToList(),
            });
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error($"Invalid task graph: {ex.Message}");
        }
    }
}

/// <summary>
/// MAF 方法参数用的 DAG 任务输入模型。
/// 框架会根据此类型自动生成 JSON Schema。
/// </summary>
public sealed class AgentWorkflowTaskInput
{
    [Description("Unique task identifier within this call (auto-assigned if omitted)")]
    public string? Id { get; set; }

    [Description("Prompt / instructions for the sub-agent")]
    public string Prompt { get; set; } = "";

    [Description("Agent type (default: general-purpose)")]
    public string? Agent { get; set; }

    [Description("Human-readable task label")]
    public string? Description { get; set; }

    [Description("IDs of tasks that must finish before this one starts")]
    public string[]? DependsOn { get; set; }

    [Description("Workspace access required by this task. ReadOnly tasks may run concurrently; Write tasks are serialized in stable topological order.")]
    public AgentTaskExecutionAccess ExecutionAccess { get; set; } = AgentTaskExecutionAccess.ReadOnly;
}
