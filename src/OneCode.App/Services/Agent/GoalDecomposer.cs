using Microsoft.Extensions.AI;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL 模式分解器：负责将用户的高层目标分解为子目标列表，以及失败后的重规划。
/// 抽取自原 Goal 外层执行器，独立成类以便单独演进与测试。
///
/// 设计说明：
/// - 不走 AgentPipelineBuilder（decompose/replan 不需要工具循环、权限检查等重型中间件）
/// - 补齐了审计日志 + token 统计，避免 decompose/replan 成为治理盲区
/// - 失败时返回 fallback 单目标计划，保证 Goal 工作流能继续执行
/// </summary>
internal interface IGoalPlanningService
{
    Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error, bool UsedFallback)>
        DecomposeWithFallbackAsync(string goal, string? modelId, CancellationToken ct);
    Task<(List<GoalItem> RemainingGoals, long InputTokens, long OutputTokens)?> ReplanAsync(
        string originalGoal,
        GoalPlan currentPlan,
        int failedGoalIndex,
        IReadOnlyList<SubGoalExecution> executions,
        string? modelId,
        CancellationToken ct);
    Task<(List<GoalItem> SubGoals, long InputTokens, long OutputTokens)?> DecomposeSubGoalAsync(
        GoalItem parent,
        int nextId,
        string? modelId,
        CancellationToken ct);
}

internal sealed class GoalDecomposer(
    IChatClient chatClient,
    ILogger<GoalDecomposer> logger,
    IPromptManager promptManager) : IGoalPlanningService
{
    private readonly IChatClient _chatClient = chatClient;
    private readonly ILogger<GoalDecomposer> _logger = logger;
    private readonly IPromptManager _promptManager = promptManager;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // GetResponseAsync<T>（结构化路径）内部会 MakeReadOnly()，未显式指定解析器的实例会在那里抛异常，
        // 且异常会被 StructuredChatCall 的拒绝重试宽捕获吞掉、伪装成网关拒绝。见 StructuredChatCall 的守卫。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// H3: 从模型输出中提取首个配对的 JSON 对象（深度扫描，正确处理字符串字面量与转义）。
    /// 兼容 markdown 代码围栏（```json ... ```）与前后杂文本。无配对块返回 null。
    /// </summary>
    internal static string? ExtractJsonBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                continue;
            }
            if (current == '{') depth++;
            else if (current == '}' && --depth == 0)
                return text[start..(index + 1)];
        }
        return null;
    }

    /// <summary>H3: 围栏剥离 + 反序列化；JsonException 一律按"解析失败"返回 null，由上层回退。</summary>
    private static GoalPlan? TryDeserializePlan(string? text)
    {
        var json = ExtractJsonBlock(text);
        if (json is null)
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<GoalPlan>(json, JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 分解目标，失败时回退到单目标计划。
    /// </summary>
    public async Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error, bool UsedFallback)>
        DecomposeWithFallbackAsync(string goal, string? modelId, CancellationToken ct)
    {
        var (plan, inputTokens, outputTokens, error) = await DecomposeAsync(goal, modelId, ct).ConfigureAwait(false);
        if (error is null && plan.Goals.Count > 0)
            return (plan, inputTokens, outputTokens, null, false);

        _logger.LogWarning("Decomposition failed ({Error}), falling back to single-goal execution", error);
        var fallbackPlan = new GoalPlan
        {
            Goals = new List<GoalItem>
            {
                new()
                {
                    Id = 1,
                    Description = goal,
                    SuccessCriteria = "Complete the user's original request as fully as possible.",
                },
            },
        };
        return (fallbackPlan, inputTokens, outputTokens, error, true);
    }

    /// <summary>
    /// 子目标失败后重规划剩余子目标。
    /// 保守策略：整个 Goal 执行周期最多重规划一次（避免无限循环）；
    /// 重规划失败则返回 null，调用方继续按原计划执行。
    /// </summary>
    public async Task<(List<GoalItem> RemainingGoals, long InputTokens, long OutputTokens)?>
        ReplanAsync(
            string originalGoal,
            GoalPlan currentPlan,
            int failedGoalIndex,
            IReadOnlyList<SubGoalExecution> executions,
            string? modelId,
            CancellationToken ct)
    {
        try
        {
            var decomposerPrompt = await _promptManager.GetPromptAsync("system/goal-decomposer", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mode prompt 'system/goal-decomposer' not found in any IPromptManager store.");

            // 构建重规划上下文：已完成子目标摘要 + 失败子目标原因 + 剩余子目标
            var completedGoals = currentPlan.Goals
                .Where(g => g.Status == GoalStatus.Completed)
                .Select(g => $"  #{g.Id}: {g.Description} (completed)")
                .ToList();

            var failedGoal = currentPlan.Goals[failedGoalIndex];
            var failedExecution = executions.FirstOrDefault(e => e.GoalId == failedGoal.Id);
            var failedInfo = $"  #{failedGoal.Id}: {failedGoal.Description} (FAILED — {failedExecution?.Evaluation ?? "unknown reason"})";

            var remainingGoals = currentPlan.Goals
                .Where((g, idx) => idx > failedGoalIndex && g.Status == GoalStatus.Pending)
                .Select(g => $"  #{g.Id}: {g.Description}")
                .ToList();

            var replanPrompt = $"""
                Original goal: {originalGoal}

                The following sub-goals have been completed:
                {string.Join("\n", completedGoals)}

                The following sub-goal FAILED and cannot be retried:
                {failedInfo}

                The following sub-goals were originally planned but may need adjustment:
                {string.Join("\n", remainingGoals)}

                Based on the progress so far and the failed sub-goal, replan the remaining
                sub-goals. You may modify, add, or remove sub-goals to account for the failure.
                Keep sub-goals that are still relevant. Output the SAME JSON format as before.
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, decomposerPrompt),
                new(ChatRole.User, replanPrompt),
            };

            var chatOptions = CreateStructuredChatOptions(modelId);

            var call = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-replanner", replanPrompt, ct).ConfigureAwait(false);

            var newPlan = call.Value ?? TryDeserializePlan(call.Text);

            if (newPlan?.Goals is null || newPlan.Goals.Count == 0)
            {
                _logger.LogWarning("Replanning returned empty plan, keeping original remaining goals");
                return null;
            }

            // 重新编号子目标 ID（从失败子目标 ID + 1 开始）
            var baseId = failedGoal.Id + 1;
            var replannedGoals = newPlan.Goals.ToList();
            for (int i = 0; i < replannedGoals.Count; i++)
                replannedGoals[i] = replannedGoals[i] with { Id = baseId + i };

            _logger.LogInformation(
                "Replanning succeeded: {Count} new sub-goals replacing original remaining goals",
                replannedGoals.Count);

            return (replannedGoals, call.InputTokens, call.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Replanning failed, continuing with original remaining goals");
            return null;
        }
    }

    /// <summary>
    /// 对单个子目标进行按需递归分解。
    /// 当 Goal 工作流执行到 <see cref="GoalItem.NeedsFurtherDecomposition"/>=true 的子目标时调用此方法。
    /// 返回分解后的子目标列表（扁平，子目标 Depth = parent.Depth + 1）。
    /// 失败时返回 null，调用方按原计划执行父目标。
    /// </summary>
    /// <param name="parent">待分解的父子目标。</param>
    /// <param name="nextId">子目标起始 ID（由调用方根据当前 GoalList 大小计算）。</param>
    /// <param name="modelId">当前实际模型 ID，透传到 ChatOptions.ModelId；response_format 能力协商在
    /// 每次调用内由 <see cref="StructuredChatCall"/> 完成（schema 请求 → 文本降级 → 无格式重试）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<(List<GoalItem> SubGoals, long InputTokens, long OutputTokens)?>
        DecomposeSubGoalAsync(GoalItem parent, int nextId, string? modelId, CancellationToken ct)
    {
        try
        {
            var decomposerPrompt = await _promptManager.GetPromptAsync("system/goal-decomposer", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mode prompt 'system/goal-decomposer' not found in any IPromptManager store.");

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, decomposerPrompt),
                new(ChatRole.User, parent.Description),
            };

            var chatOptions = CreateStructuredChatOptions(modelId);

            var call = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-sub-decomposer", parent.Description, ct).ConfigureAwait(false);

            var newPlan = call.Value ?? TryDeserializePlan(call.Text);

            if (newPlan?.Goals is null || newPlan.Goals.Count == 0)
            {
                _logger.LogWarning("Sub-goal decomposition returned empty plan for parent #{Id}", parent.Id);
                return null;
            }

            // 子目标 Depth = parent.Depth + 1，ID 从 nextId 开始连续编号
            var subGoals = newPlan.Goals
                .Select((g, i) => g with { Id = nextId + i, Depth = parent.Depth + 1 })
                .ToList();

            _logger.LogInformation(
                "Sub-goal #{ParentId} decomposed into {Count} sub-goals at depth {Depth}",
                parent.Id, subGoals.Count, parent.Depth + 1);

            return (subGoals, call.InputTokens, call.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sub-goal decomposition failed for parent #{Id}", parent.Id);
            return null;
        }
    }

    private async Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error)> DecomposeAsync(
        string goal, string? modelId, CancellationToken ct)
    {
        try
        {
            var decomposerPrompt = await _promptManager.GetPromptAsync("system/goal-decomposer", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mode prompt 'system/goal-decomposer' not found in any IPromptManager store.");

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, decomposerPrompt),
                new(ChatRole.User, goal),
            };

            var chatOptions = CreateStructuredChatOptions(modelId);

            var call = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-decomposer", goal, ct).ConfigureAwait(false);

            // 结构化优先（原生 schema），失败走 ExtractJsonBlock 文本降级
            var plan = call.Value ?? TryDeserializePlan(call.Text);
            if (plan is not null)
                return (plan, call.InputTokens, call.OutputTokens, Error: null);

            // 空文本通常意味着推理模型把 token 预算耗在 reasoning_content 上；
            // 解析失败则说明输出里没有可提取的 JSON 计划块。两者都给出可诊断的错误，
            // 供 DecomposeWithFallbackAsync 的 Warning 日志使用。
            return (new GoalPlan(), call.InputTokens, call.OutputTokens,
                Error: string.IsNullOrWhiteSpace(call.Text)
                    ? $"model returned empty text (reasoning budget likely exhausted; maxOutput={chatOptions.MaxOutputTokens})"
                    : "model output contained no parseable JSON plan block");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Goal decomposition failed");
            return (new GoalPlan(), 0, 0, Error: ex.Message);
        }
    }

    private static ChatOptions CreateStructuredChatOptions(string? modelId) => new()
    {
        ModelId = modelId,
        MaxOutputTokens = 8192,
        // ResponseFormat 不在此处设置：能力协商统一由 StructuredChatCall 完成
        //（schema 请求 → 同响应文本降级 → 硬拒绝时无格式重试）。goal-decomposer 系统提示词
        // 自带完整 JSON 契约，正是无格式重试路径（可移植基线）的契约来源。
    };

    /// <summary>
    /// 结构化 LLM 调用的统一辅助方法（"结构化请求 + 文本降级"，见 <see cref="StructuredChatCall"/>）。
    /// 为 decompose/replan 等轻量级 LLM 调用提供：
    /// - 审计日志（记录调用阶段、输入长度、输出长度、token 用量、是否经由原生 schema）
    /// - 统一 token 统计（从 response.Usage 提取并返回）
    ///
    /// 返回值：<see cref="StructuredChatResult{T}.Value"/> 为原生 schema 路径的结果；
    /// 为空时调用方用 <see cref="StructuredChatResult{T}.Text"/> 走 <see cref="TryDeserializePlan"/> 降级解析。
    /// </summary>
    private async Task<StructuredChatResult<GoalPlan>> RunStructuredLlmCallAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions chatOptions,
        string stageName,
        string userPrompt,
        CancellationToken ct)
    {
        // 审计日志：调用前记录阶段和输入规模
        var inputChars = messages.Sum(m => m.Text?.Length ?? 0);
        _logger.LogInformation(
            "Goal LLM call starting: stage={Stage}, inputChars={Chars}, maxOutput={MaxTokens}",
            stageName, inputChars, chatOptions.MaxOutputTokens);

        // GoalDecomposer 是直连模型路径（不构造 agent 生命周期），不在 Hook 6 个拦截点范围内。

        var call = await StructuredChatCall.CallAsync<GoalPlan>(
            _chatClient, messages, chatOptions, JsonOptions, _logger, ct).ConfigureAwait(false);

        // 审计日志：调用后记录 token 用量、输出规模与结构化路径
        _logger.LogInformation(
            "Goal LLM call completed: stage={Stage}, inputTokens={InputTokens}, outputTokens={OutputTokens}, outputChars={OutputChars}, viaSchema={ViaSchema}",
            stageName, call.InputTokens, call.OutputTokens, call.Text.Length, call.ViaSchema);

        return call;
    }
}
