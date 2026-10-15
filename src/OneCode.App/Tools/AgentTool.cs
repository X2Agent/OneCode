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
    CustomAgentCatalog? customAgents = null,
    IToolCatalog? toolCatalog = null,
    ILogger<AgentTool>? logger = null)
{
    /// <summary>基础描述（不含自定义代理清单）；工具注册处以此 + 清单动态合成最终 description。</summary>
    public const string BaseDescription =
        "Spawn a sub-agent to handle a delegated coding or analysis task in a separate session, returning its final output when complete. " +
        "Use this to parallelize independent investigation work (e.g. 'research the auth flow while I draft the API spec') or to delegate a self-contained subtask. " +
        "The sub-agent runs with its OWN system prompt and tool access; it does NOT inherit the current conversation history. " +
        "Write self-contained prompts: include file paths, line numbers, and exact instructions — the agent cannot see your prior messages. " +
        "Synchronous mode (default): blocks until the sub-agent finishes and returns its output. " +
        "Background mode (runInBackground=true): returns a taskId immediately; use Task tool with action='output' to poll or action='stop' to cancel. " +
        "Failure handling: exceptions in the sub-agent are logged and surfaced as an error result, not thrown to the caller. " +
        "Do NOT use this tool to ask simple questions you can answer yourself, or to trivially report file contents.";

    /// <summary>合成最终工具 description：基础描述 + 自定义代理清单（保证可发现性）。</summary>
    public string BuildDescription()
    {
        var custom = customAgents?.GetAll();
        if (custom is not { Count: > 0 })
            return BaseDescription;

        return BaseDescription +
            "\n\nCustom agents (from .onecode/agents/*.md, project overrides user level): " +
            string.Join("; ", custom.Select(a => $"'{a.Name}' — {a.Description}"));
    }

    [Description(AgentTool.BaseDescription)]
    public async Task<ToolResult> RunAgentAsync(
        [Description("Detailed, self-contained instructions for the sub-agent. Include all context the agent needs (file paths, line numbers, requirements) since it cannot see your conversation history.")] string prompt,
        [Description("Agent type: 'general-purpose' (default, full tool access), 'Explore' (read-only research), 'Plan' (designs implementation approaches), " +
                     "or a custom agent name defined in .onecode/agents/*.md (project) / ~/.onecode/agents/*.md (user).")] string agent = "general-purpose",
        [Description("Short one-line description of the subtask, shown in task tracking UI. Not shown to the sub-agent.")] string? description = null,
        [Description("When true, the agent runs as a background task and returns a taskId immediately instead of blocking. Use Task tool (action='output') to poll for results and (action='stop') to cancel.")] bool runInBackground = false,
        [Description("Optional turn limit for the sub-agent (model round-trips). Can only LOWER the limit below the configured ceiling (agent frontmatter / settings); omit to use the configured default.")] int? maxTurns = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("prompt is required");

        var (definition, resolutionError) = CustomAgentResolver.Resolve(customAgents, agent, toolCatalog);
        if (resolutionError is not null)
            return ToolResult.Error(resolutionError);

        // 引擎侧已在 run 开始前写入 CacheSafe 快照（QueryStreamEngine.StreamCoreAsync），
        // 这里仍可能为 null 的残留场景（未过引擎的宿主路径）值得留痕——空工具表会让
        // 子代理静默失去全部父级工具，日志是唯一线索。
        if (cacheSafeParams.Current is null)
            logger?.LogWarning("Agent fork '{Agent}' started without parent CacheSafeParams; child tool filtering will yield an empty tool list", agent);

        var agentRequest = new AgentRunRequest(
            Prompt: prompt,
            Agent: agent,
            Description: description,
            MaxTurns: maxTurns,
            CacheSafeParams: cacheSafeParams.Current,
            ParentCapabilities: ToolActivationContext.CurrentCapabilities,
            Instructions: definition?.Instructions,
            AllowedToolNames: definition?.AllowedTools,
            MaxTurnsCeiling: definition?.MaxTurns);

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
                // partial 结果必须显式标注：输出只覆盖已完成的调研/改动，
                // 不标注会让父代理把半成品当作最终产出直接交付。措辞只陈述事实，
                // 不暗示"续派"——重复派工同一任务会重跑同样的检索并再次撞上限。
                result = result.MaxTurnsReached
                    ? $"[PARTIAL] The sub-agent stopped at its turn/time budget. Findings so far (use them; do not re-dispatch the same task):\n{result.Output}"
                    : result.Output,
                turnsCompleted = result.TurnsCompleted,
                maxTurnsReached = result.MaxTurnsReached,
                toolCalls = result.ToolCallCount,
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
