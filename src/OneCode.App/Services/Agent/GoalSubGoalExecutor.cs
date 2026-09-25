using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Lsp;
using OneCode.App.Tui;
using OneCode.Core.Coordinator;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL sub-goal execution facade (W4): builds the inner AIAgent, then runs MAF
/// <see cref="LoopAgent"/> via <see cref="GoalSubGoalLoop"/>. Hard gates and semantic
/// judge live in <see cref="GoalSubGoalHardGate"/> / <see cref="GoalSubGoalJudge"/>.
/// Prefer DelegateLoopEvaluator over AIJudgeLoopEvaluator — see plan §2.6 / §14.
/// </summary>
internal interface IGoalStepExecutionService
{
    Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        CancellationToken ct);
    void UpdateGoalContext(
        GoalPlan plan,
        GoalItem currentGoal,
        IReadOnlyList<SubGoalExecution> executions,
        bool sharedTransactionOwned);
    Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
        string originalGoal,
        IReadOnlyList<GoalItem> goals,
        IReadOnlyList<SubGoalExecution> executions,
        CancellationToken ct);
}

internal sealed class GoalSubGoalExecutor : IGoalStepExecutionService
{
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly ILogger<GoalSubGoalExecutor> _logger;
    private readonly IPromptManager _promptManager;
    private readonly GoalContextState _goalContextState;
    private readonly GoalSubGoalLoop _loop;
    private readonly GoalSubGoalJudge _judge;

    public GoalSubGoalExecutor(
        IMainAgentRunner mainAgentRunner,
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        ILogger<GoalSubGoalExecutor> logger,
        IPromptManager promptManager,
        GoalContextState goalContextState,
        IVerificationProvider? verificationProvider = null,
        LspDiagnosticRegistry? diagnosticRegistry = null)
    {
        _mainAgentRunner = mainAgentRunner;
        _logger = logger;
        _promptManager = promptManager;
        _goalContextState = goalContextState;
        var hardGate = new GoalSubGoalHardGate(verificationProvider, diagnosticRegistry);
        _judge = new GoalSubGoalJudge(chatClient, promptManager);
        _loop = new GoalSubGoalLoop(hardGate, _judge, loggerFactory);
    }

    public async Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventWriter);
        try
        {
            var toolExecutions = new List<GoalToolExecutionEvidence>();
            var userPrompt = await BuildSubGoalPromptAsync(goal, ct).ConfigureAwait(false);
            var runOptions = await BuildSubGoalRunOptionsAsync(
                goal,
                options,
                sharedTransaction,
                userPrompt,
                evt => GoalSubGoalAssessment.CaptureToolEvidence(evt, toolExecutions),
                ct).ConfigureAwait(false);
            var innerAgent = await _mainAgentRunner.BuildAsAIAgentAsync(runOptions, ct).ConfigureAwait(false);
            return await _loop.RunAsync(
                innerAgent,
                goal,
                options,
                sharedTransaction,
                eventWriter,
                toolExecutions,
                runOptions.SystemPrompt ?? string.Empty,
                userPrompt,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sub-goal {Id} streaming execution failed unexpectedly", goal.Id);
            goal.Status = GoalStatus.Failed;
            return new SubGoalExecution(
                GoalId: goal.Id,
                Status: GoalStatus.Failed,
                Attempts: 1,
                InputTokens: 0,
                OutputTokens: 0,
                AgentOutput: "",
                Evaluation: $"Unexpected error: {ex.Message}");
        }
    }

    public Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
        string originalGoal,
        IReadOnlyList<GoalItem> goals,
        IReadOnlyList<SubGoalExecution> executions,
        CancellationToken ct)
        => _judge.EvaluateFinalGoalAsync(originalGoal, goals, executions, ct);

    /// <summary>
    /// 更新 GOAL 模式共享上下文（供子目标执行期间 system prompt 注入使用）。
    /// </summary>
    public void UpdateGoalContext(GoalPlan plan, GoalItem currentGoal, IReadOnlyList<SubGoalExecution> executions, bool sharedTransactionOwned)
    {
        var completedSummaries = executions
            .Where(e => e.Status == GoalStatus.Completed)
            .Select(e => (e.GoalId, plan.Goals.FirstOrDefault(g => g.Id == e.GoalId)?.Description ?? "", GoalSubGoalAssessment.TruncateForSummary(e.AgentOutput)))
            .ToList();

        var failedSummaries = executions
            .Where(e => e.Status == GoalStatus.Failed)
            .Select(e => (e.GoalId, plan.Goals.FirstOrDefault(g => g.Id == e.GoalId)?.Description ?? "", e.Evaluation))
            .ToList();

        var snapshot = new GoalContextSnapshot(
            CurrentGoalId: currentGoal.Id,
            TotalGoals: plan.Goals.Count,
            CompletedSummaries: completedSummaries,
            FailedSummaries: failedSummaries,
            SharedTransactionHint: true,
            CurrentGoalDepth: currentGoal.Depth);

        _goalContextState.Update(snapshot);
    }

    /// <summary>
    /// 构造子目标的 <see cref="MainAgentRunOptions"/>。
    /// </summary>
    private async Task<MainAgentRunOptions> BuildSubGoalRunOptionsAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        string userPrompt,
        Action<OrchestrationEvent> evidenceSink,
        CancellationToken ct)
    {
        // Load sub-goal execution system prompt from IPromptManager (three-layer store:
        // project > user > built-in). Throws if missing — prompts/system/goal-subgoal.prompt
        // is always shipped via csproj Content copy, so this should never fail in production.
        var systemPrompt = await _promptManager.GetPromptAsync("system/goal-subgoal", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mode prompt 'system/goal-subgoal' not found in any IPromptManager store.");

        // 按子目标 requiredTools 裁剪工具集。
        // null/空/"*" → 全量工具集；否则按名称过滤。
        var filteredTools = FilterToolsForSubGoal(options.Tools, goal.RequiredTools);

        return new MainAgentRunOptions
        {
            SystemPrompt = systemPrompt,
            // No product harness fragment: system/goal-subgoal.prompt is self-contained (it ships its
            // own prompt-injection defense) and was never composed with the shared fragment. Suppression
            // rather than null so MAF does not inject DefaultInstructions on top of it — the §4.6
            // migration must not change prompt text as a side effect, and pre-migration the defaults
            // were suppressed.
            HarnessInstructions = OneCodeHarnessDefaults.SuppressFrameworkDefaults,
            UserPrompt = userPrompt,
            ModelId = options.ModelId,
            WorkingDirectory = options.WorkingDirectory,
            MaxTurns = options.MaxTurnsPerSubGoal,
            Tools = filteredTools,
            WorkingMode = WorkingMode.Goal,
            SharedTransaction = sharedTransaction,
            // 同一事件同时进入 UI 和确定性验证证据收集器。
            OrchestrationEventSink = evt =>
            {
                evidenceSink(evt);
                options.OrchestrationEventSink?.Invoke(evt);
            },
        };
    }

    /// <summary>
    /// 按子目标 RequiredTools 白名单过滤工具集。
    /// 规则：
    /// - RequiredTools 为 null/空 → 返回全量工具集（不裁剪）
    /// - RequiredTools 包含 "*" → 返回全量工具集（显式通配）
    /// - 否则 → 仅保留名称匹配的工具
    /// - 过滤后为空 → 抛出异常（fail-closed），不回退到全量工具集
    /// </summary>
    private static IList<AITool> FilterToolsForSubGoal(
        IList<AITool> allTools,
        IReadOnlyList<string>? requiredTools)
    {
        if (requiredTools is null || requiredTools.Count == 0)
            return allTools;

        if (requiredTools.Contains("*"))
            return allTools;

        var allowedSet = new HashSet<string>(requiredTools, StringComparer.Ordinal);
        var filtered = allTools
            .Where(t => t is AIFunction af && allowedSet.Contains(af.Name))
            .ToList();

        // P2-1: fail-closed — do not fall back to allTools when no match is found.
        // Unknown tool names in the plan indicate a decomposition error; using allTools
        // would silently expand permissions beyond what the plan intended.
        if (filtered.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid tools found for requiredTools [{string.Join(", ", requiredTools)}]. " +
                "The goal decomposition produced unknown tool names. " +
                "Fix the plan or use '*' to explicitly allow all tools.");
        }

        return filtered;
    }

    /// <summary>
    /// 生成子目标的初始 user prompt。
    /// 反馈注入由 <see cref="LoopAgent"/> 通过 <c>FeedbackMessageTemplate</c> 自动处理，
    /// 无需在此方法中拼接 feedback。
    /// </summary>
    private Task<string> BuildSubGoalPromptAsync(GoalItem goal, CancellationToken ct)
    {
        var prompt =
            $"""
            ## Sub-goal {goal.Id}: {goal.Description}

            Success criteria: {goal.SuccessCriteria}

            Execute this sub-goal. When done, clearly state whether the success criteria
            were met, and provide specific evidence (e.g., test results, file changes,
            command output) to support your conclusion.
            """;

        return Task.FromResult(prompt);
    }

}
