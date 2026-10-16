using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Core.Coordinator;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL sub-goal execution facade (W4): builds the inner AIAgent, then runs MAF
/// <see cref="LoopAgent"/> via <see cref="GoalSubGoalLoop"/>. Hard gates and semantic
/// judge live in <see cref="GoalSubGoalHardGate"/> / <see cref="GoalSubGoalJudge"/>.
/// Prefer DelegateLoopEvaluator over AIJudgeLoopEvaluator.
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
}

internal sealed class GoalSubGoalExecutor : IGoalStepExecutionService
{
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly ILogger<GoalSubGoalExecutor> _logger;
    private readonly IPromptManager _promptManager;
    private readonly GoalContextState _goalContextState;
    private readonly GoalSubGoalLoop _loop;
    private readonly GoalSubTaskDagRunner _dagRunner;

    public GoalSubGoalExecutor(
        IMainAgentRunner mainAgentRunner,
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        IPromptManager promptManager,
        GoalContextState goalContextState,
        GoalSubGoalGateDependencies gateDependencies,
        GoalSubTaskDagRunner dagRunner,
        TodoProjectionService? todoProjection = null)
    {
        _mainAgentRunner = mainAgentRunner;
        _logger = loggerFactory.CreateLogger<GoalSubGoalExecutor>();
        _promptManager = promptManager;
        _goalContextState = goalContextState;
        _dagRunner = dagRunner;
        var hardGate = new GoalSubGoalHardGate(
            gateDependencies.VerificationProvider,
            gateDependencies.DiagnosticRegistry);
        var judge = new GoalSubGoalJudge(chatClient, promptManager);
        _loop = new GoalSubGoalLoop(hardGate, judge, loggerFactory, todoProjection);
    }

    public async Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventWriter);

        // 步内并行分支：subTasks 非空时走共享 DAG 执行器（fork 自管事务，step 级事务空壳）。
        if (goal.SubTasks is { Count: > 0 })
        {
            using var dagCwdScope = OneCodeAgentRunContext.UseWorkingDirectory(options.WorkingDirectory);
            return await _dagRunner.RunAsync(goal, options, eventWriter, ct).ConfigureAwait(false);
        }

        try
        {
            using var cwdScope = OneCodeAgentRunContext.UseWorkingDirectory(options.WorkingDirectory);
            List<GoalToolExecutionEvidence> toolExecutions = [];
            var userPrompt = await BuildSubGoalPromptAsync(goal, options.WorkingDirectory, ct).ConfigureAwait(false);
            var runOptions = await BuildSubGoalRunOptionsAsync(
                _promptManager,
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
    internal static async Task<MainAgentRunOptions> BuildSubGoalRunOptionsAsync(
        IPromptManager promptManager,
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        string userPrompt,
        Action<OrchestrationEvent> evidenceSink,
        CancellationToken ct)
    {
        // Load the shared harness fragment from IPromptManager (three-layer store:
        // project > user > built-in). Mode-specific Goal instructions are injected once per turn
        // by ModeInstructionProvider under WorkingMode.Goal (system/goal.prompt).
        var harnessPrompt = await promptManager.GetPromptAsync(PromptComposer.HarnessPromptName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Harness prompt '{PromptComposer.HarnessPromptName}' not found in any IPromptManager store.");

        // 按子目标 requiredTools 裁剪工具集。
        // null/空/"*" → 全量工具集；否则按名称过滤。
        var filteredTools = FilterToolsForSubGoal(options.Tools, goal.RequiredTools);

        return new MainAgentRunOptions
        {
            SystemPrompt = options.SystemPrompt,
            HarnessInstructions = harnessPrompt,
            UserPrompt = userPrompt,
            ModelId = options.ModelId,
            WorkingDirectory = options.WorkingDirectory,
            MaxTurns = options.MaxTurnsPerSubGoal,
            Tools = filteredTools,
            WorkingMode = WorkingMode.Goal,
            SharedTransaction = sharedTransaction,
            // GOAL 子目标在 LoopAgent 内无交互式审批通道，必须禁用 ApprovalRequiredAIFunction 包装，
            // 让权限/路径错误直接作为 ToolResult.Error 返回给 LLM 自纠，而非因 pending approval 中止循环。
            SuppressToolApproval = true,
            // 同一事件同时进入 UI 和确定性验证证据收集器。
            OrchestrationEventSink = evt =>
            {
                evidenceSink(evt);
                options.OrchestrationEventSink?.Invoke(evt);
            },
        };
    }

    /// <summary>
    /// 工作区基础读写与探查工具集合：当子目标显式声明了 <c>RequiredTools</c> 时，
    /// 始终保底并入这组基础工具，防止模型写出文件后因缺少 <c>Read</c>/<c>Glob</c> 无法自检而被迫调用无关工具。
    /// </summary>
    internal static readonly IReadOnlySet<string> BaselineWorkspaceTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "Read",
        "Glob",
        "Grep",
        "LS",
        "Edit",
        "Write",
    };

    /// <summary>
    /// 按子目标 RequiredTools 白名单过滤工具集。
    /// 规则：
    /// - RequiredTools 为 null/空 → 返回全量工具集（不裁剪）
    /// - RequiredTools 包含 "*" → 返回全量工具集（显式通配）
    /// - 否则 → 验证声明的工具名至少命中一个非派工工具，随后并入 <see cref="BaselineWorkspaceTools"/> 保底工作区基础工具
    /// - 声明的工具名无一命中 → 抛出异常（fail-closed），不回退到全量工具集
    /// 所有路径无条件摘除派工工具（<see cref="ToolCapabilitySet.SubAgentDispatchTools"/>）：
    /// 子目标不可再派工，与「嵌套派工深度上限 1」的 fork 路径语义一致
    ///（docs/sub-agents.md「所有子代理的工具面无条件摘除」）。
    /// </summary>
    internal static IList<AITool> FilterToolsForSubGoal(
        IList<AITool> allTools,
        IReadOnlyList<string>? requiredTools)
    {
        if (requiredTools is null || requiredTools.Count == 0)
            return WithoutSubAgentDispatchTools(allTools);

        if (requiredTools.Contains("*"))
            return WithoutSubAgentDispatchTools(allTools);

        var requestedSet = new HashSet<string>(requiredTools, StringComparer.Ordinal);
        var requestedMatches = allTools
            .Where(t => t is AIFunction af && requestedSet.Contains(af.Name))
            .ToList();

        // P2-1: fail-closed — do not fall back to allTools when no match is found.
        // Unknown tool names in the plan indicate a decomposition error; using allTools
        // would silently expand permissions beyond what the plan intended.
        if (requestedMatches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid tools found for requiredTools [{string.Join(", ", requiredTools)}]. " +
                "The goal decomposition produced unknown tool names. " +
                "Fix the plan or use '*' to explicitly allow all tools.");
        }

        var requestedWithoutDispatch = WithoutSubAgentDispatchTools(requestedMatches);
        if (requestedWithoutDispatch.Count == 0)
        {
            // fail-closed：分解器把派工工具写进了 RequiredTools——子目标工具面摘除后为空集，
            // 不得静默零工具运行。
            throw new InvalidOperationException(
                $"requiredTools [{string.Join(", ", requiredTools)}] only matched sub-agent dispatch tools, " +
                "which are never available to sub-goals (nested dispatch depth limit 1). " +
                "Fix the plan: dispatch is a main-agent capability.");
        }

        var allowedWithBaseline = new HashSet<string>(requestedSet, StringComparer.Ordinal);
        allowedWithBaseline.UnionWith(BaselineWorkspaceTools);

        var filtered = allTools
            .Where(t => t is AIFunction af && allowedWithBaseline.Contains(af.Name))
            .ToList();

        return WithoutSubAgentDispatchTools(filtered);
    }

    /// <summary>摘除派工工具（Agent / ParallelAgents）——子目标不可再派工。</summary>
    private static IList<AITool> WithoutSubAgentDispatchTools(IList<AITool> tools)
        => tools
            .Where(t => t is not AIFunction af || !ToolCapabilitySet.SubAgentDispatchTools.Contains(af.Name))
            .ToList();

    /// <summary>
    /// 生成子目标的初始 user prompt，显式告知工作目录与允许修改的路径范围（AllowedPaths），
    /// 防止执行代理在不知情下写出越界文件触发 change-scope 硬门禁失败。
    /// </summary>
    private Task<string> BuildSubGoalPromptAsync(GoalItem goal, string workingDirectory, CancellationToken ct)
    {
        var scopeInstruction = goal.AllowedPaths is { Count: > 0 }
            ? $"Allowed modification scope (workspace-relative): {string.Join(", ", goal.AllowedPaths)} (do not modify or leave files outside this scope)."
            : $"Allowed modification scope: within working directory ({workingDirectory}).";

        var prompt =
            $"""
            ## Sub-goal {goal.Id}: {goal.Description}

            Success criteria: {goal.SuccessCriteria}
            Working directory: {workingDirectory}
            {scopeInstruction}

            Execute this sub-goal. When done, clearly state whether the success criteria
            were met, and provide specific evidence (e.g., test results, file changes,
            command output) to support your conclusion.
            """;

        return Task.FromResult(prompt);
    }

}
