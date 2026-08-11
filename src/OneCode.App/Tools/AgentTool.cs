using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.Core.Tasks;

namespace OneCode.App.Tools;

/// <summary>
/// 子代理委派工具 — 通过 <see cref="IAgentRunner"/> 在独立会话中执行任务。
/// MAF 原生模式：普通类 + [Description] 标注方法。
/// </summary>
public sealed class AgentTool(
    IAgentRunner runner,
    ICacheSafeParamsProvider cacheSafeParams,
    ITaskService taskService,
    ILogger<AgentTool>? logger = null)
{
    [Description("Spawn a sub-agent to handle a delegated coding or analysis task in a separate session, returning its final output when complete. " +
                 "Use this to parallelize independent investigation work (e.g. 'research the auth flow while I draft the API spec') or to delegate a self-contained subtask. " +
                 "The sub-agent runs with its OWN system prompt and tool access; it does NOT inherit the current conversation history. " +
                 "Write self-contained prompts: include file paths, line numbers, and exact instructions — the agent cannot see your prior messages. " +
                 "Synchronous mode (default): blocks until the sub-agent finishes and returns its output. " +
                 "Background mode (runInBackground=true): returns a taskId immediately; use Task tool with action='output' to poll or action='stop' to cancel. " +
                 "Failure handling: exceptions in the sub-agent are logged and surfaced as an error result, not thrown to the caller. " +
                 "Do NOT use this tool to ask simple questions you can answer yourself, or to trivially report file contents.")]
    public async Task<ToolResult> RunAgentAsync(
        [Description("Detailed, self-contained instructions for the sub-agent. Include all context the agent needs (file paths, line numbers, requirements) since it cannot see your conversation history.")] string prompt,
        [Description("Built-in agent type: 'general-purpose' (default, full tool access), 'Explore' (read-only research), 'Plan' (designs implementation approaches). " +
                     "Choose based on the task: Explore for codebase research, Plan for design, general-purpose for implementation.")] string agent = "general-purpose",
        [Description("Short one-line description of the subtask, shown in task tracking UI. Not shown to the sub-agent.")] string? description = null,
        [Description("When true, the agent runs as a background task and returns a taskId immediately instead of blocking. Use Task tool (action='output') to poll for results and (action='stop') to cancel.")] bool runInBackground = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("prompt is required");

        var agentRequest = new AgentRunRequest(
            Prompt: prompt,
            Agent: agent,
            Description: description,
            CacheSafeParams: cacheSafeParams.Current,
            ParentCapabilities: ToolActivationContext.CurrentCapabilities);

        if (runInBackground)
        {
            var taskItem = taskService.CreateTask(
                subject: description ?? $"Agent: {agent}",
                description: prompt.Length > 200 ? string.Concat(prompt.AsSpan(0, 200), "...") : prompt,
                status: OneCode.Core.Tasks.TaskStatus.InProgress,
                owner: agent,
                conversationId: ToolActivationContext.CurrentConversationId,
                buildRunId: OneCodeAgentRunContext.CurrentBuildRunId);

            var bgTaskId = taskItem.Id;

            // Per-task token so ESC / new input does not cancel a background agent.
            var taskToken = taskService.GetTaskToken(bgTaskId);
            var bgRequest = agentRequest with { TaskId = bgTaskId };

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await runner.RunAsync(bgRequest, taskToken).ConfigureAwait(false);

                    if (result.Error is not null)
                    {
                        taskService.AppendTaskOutput(bgTaskId,
                            $"Error: [{result.Error.Type}] {result.Error.Detail}");
                        taskService.UpdateTask(bgTaskId, status: OneCode.Core.Tasks.TaskStatus.Failed);
                    }
                    else
                    {
                        taskService.AppendTaskOutput(bgTaskId, result.Output ?? "(no output)");
                        taskService.UpdateTask(bgTaskId, status: OneCode.Core.Tasks.TaskStatus.Completed);
                    }
                }
                catch (OperationCanceledException)
                {
                    taskService.UpdateTask(bgTaskId, status: OneCode.Core.Tasks.TaskStatus.Cancelled);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "AgentTool background task {TaskId} failed", bgTaskId);
                    taskService.AppendTaskOutput(bgTaskId, $"Error: {ex.Message}");
                    taskService.UpdateTask(bgTaskId, status: OneCode.Core.Tasks.TaskStatus.Failed);
                }
            }, CancellationToken.None);

            return ToolResult.Success(JsonSerializer.Serialize(new
            {
                background = true,
                taskId = bgTaskId,
                agent,
                description,
                status = "started",
            }));
        }

        try
        {
            var result = await runner.RunAsync(agentRequest, ct).ConfigureAwait(false);

            // Structured error: parent agent can decide retry/degrade from problem.type.
            if (result.Error is not null)
            {
                return ToolResult.Success(JsonSerializer.Serialize(new
                {
                    agent = result.Agent,
                    conversationId = result.ConversationId,
                    result = (string?)null,
                    error = new
                    {
                        type = result.Error.Type,
                        title = result.Error.Title,
                        detail = result.Error.Detail,
                        traceId = result.Error.TraceId,
                        suggestedNextAction = result.Error.SuggestedNextAction,
                    },
                    turnsCompleted = result.TurnsCompleted,
                    maxTurnsReached = result.MaxTurnsReached,
                }));
            }

            return ToolResult.Success(JsonSerializer.Serialize(new
            {
                agent = result.Agent,
                conversationId = result.ConversationId,
                result = result.Output,
                turnsCompleted = result.TurnsCompleted,
                maxTurnsReached = result.MaxTurnsReached,
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "AgentTool synchronous execution failed for agent '{Agent}'", agent);
            return ToolResult.Error($"Agent execution failed: {ex.Message}");
        }
    }
}
