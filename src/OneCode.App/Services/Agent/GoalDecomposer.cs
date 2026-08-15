using Microsoft.Extensions.AI;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL 模式分解器：负责将用户的高层目标分解为子目标列表，以及失败后的重规划。
/// 抽取自原 Goal 外层执行器，独立成类以便单独演进与测试。
///
/// 设计说明：
/// - 不走 AgentPipelineBuilder（decompose/replan 不需要工具循环、权限检查等重型中间件）
/// - 补齐了审计日志 + Hook 触发 + token 统计，避免 decompose/replan 成为治理盲区
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

internal sealed class GoalDecomposer : IGoalPlanningService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<GoalDecomposer> _logger;
    private readonly IPromptManager _promptManager;
    private readonly IHookExecutionService _hookExecutionService;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public GoalDecomposer(
        IChatClient chatClient,
        ILogger<GoalDecomposer> logger,
        IPromptManager promptManager,
        IHookExecutionService hookExecutionService)
    {
        _chatClient = chatClient;
        _logger = logger;
        _promptManager = promptManager;
        _hookExecutionService = hookExecutionService;
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

            var (response, inputTokens, outputTokens) = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-replanner", replanPrompt, ct).ConfigureAwait(false);

            var json = response.Text ?? "";
            var newPlan = System.Text.Json.JsonSerializer.Deserialize<GoalPlan>(json, JsonOptions);

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

            return (replannedGoals, inputTokens, outputTokens);
        }
        catch (Exception ex)
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
    /// <param name="modelId">当前实际模型 ID，用于按模型缓存结构化输出兼容性。</param>
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

            var (response, inputTokens, outputTokens) = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-sub-decomposer", parent.Description, ct).ConfigureAwait(false);

            var json = response.Text ?? "";
            var newPlan = System.Text.Json.JsonSerializer.Deserialize<GoalPlan>(json, JsonOptions);

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

            return (subGoals, inputTokens, outputTokens);
        }
        catch (Exception ex)
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

            var (response, inputTokens, outputTokens) = await RunStructuredLlmCallAsync(
                messages, chatOptions, "goal-decomposer", goal, ct).ConfigureAwait(false);

            var json = response.Text ?? "";
            var plan = System.Text.Json.JsonSerializer.Deserialize<GoalPlan>(json, JsonOptions);

            return (plan ?? new GoalPlan(), inputTokens, outputTokens, Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Goal decomposition failed");
            return (new GoalPlan(), 0, 0, Error: ex.Message);
        }
    }

    private static ChatOptions CreateStructuredChatOptions(string? modelId) => new()
    {
        ModelId = modelId,
        MaxOutputTokens = 2048,
        // Do not set ResponseFormat here. Some OpenAI-compatible gateways reject every
        // response_format variant, and exception-driven probing still raises a first-chance
        // ClientResultException while debugging. The goal-decomposer system prompt already
        // contains the exact JSON contract, so prompt-only JSON is the portable baseline.
    };

    /// <summary>
    /// 结构化 LLM 调用的统一辅助方法。
    /// 为 decompose/replan 等轻量级 LLM 调用提供：
    /// - 审计日志（记录调用阶段、输入长度、输出长度、token 用量）
    /// - Hook 触发（UserPromptSubmit 事件，让自定义钩子感知 decompose/replan 调用）
    /// - 统一 token 统计（从 response.Usage 提取并返回）
    /// </summary>
    private async Task<(ChatResponse Response, long InputTokens, long OutputTokens)> RunStructuredLlmCallAsync(
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

        // Hook 触发：让 UserPromptSubmit 钩子感知到 decompose/judge 阶段的 LLM 调用
        if (_hookExecutionService is not null)
        {
            try
            {
                var payload = new HookPayload
                {
                    Event = HookEvent.UserPromptSubmit,
                    Cwd = Environment.CurrentDirectory,
                    UserMessage = $"[{stageName}] {userPrompt}",
                };
                await _hookExecutionService.FireAsync(payload, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Hook 失败不应阻断 LLM 调用
                _logger.LogWarning(ex, "Hook execution failed for stage {Stage}, continuing with LLM call", stageName);
            }
        }

        var response = await _chatClient.GetResponseAsync(messages, chatOptions, ct)
            .ConfigureAwait(false);

        var inputTokens = (long)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (long)(response.Usage?.OutputTokenCount ?? 0);

        // 审计日志：调用后记录 token 用量和输出规模
        _logger.LogInformation(
            "Goal LLM call completed: stage={Stage}, inputTokens={InputTokens}, outputTokens={OutputTokens}, outputChars={OutputChars}",
            stageName, inputTokens, outputTokens, response.Text?.Length ?? 0);

        return (response, inputTokens, outputTokens);
    }
}
