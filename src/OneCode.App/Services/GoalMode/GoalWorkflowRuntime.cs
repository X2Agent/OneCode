using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using OneCode.App.Services.Agent;
using OneCode.App.Query;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Goals;
using static OneCode.App.Services.GoalMode.GoalWorkflowStateMapper;

namespace OneCode.App.Services.GoalMode;

public sealed record GoalWorkflowRuntimeContext(
    GoalRunOptions Options,
    ChannelWriter<QueryEvent> EventWriter,
    Func<EditTransaction> TransactionFactory,
    bool PromptOnBudgetExhausted = false);

public interface IGoalWorkflowRuntimeFactory
{
    IGoalWorkflowRuntime Create(GoalWorkflowRuntimeContext context);
}

internal sealed class GoalWorkflowRuntimeFactory(
    IGoalPlanningService planningService,
    IGoalStepExecutionService stepExecutionService,
    IGoalRunStore goalRunStore,
    IGoalWorkspaceService workspaceService,
    IGoalCompletionService completionService,
    IBudgetDecisionService budgetDecisionService,
    ILogger<GoalWorkflowRuntime>? logger = null) : IGoalWorkflowRuntimeFactory
{
    public IGoalWorkflowRuntime Create(GoalWorkflowRuntimeContext context)
        => new GoalWorkflowRuntime(
            planningService,
            stepExecutionService,
            goalRunStore,
            workspaceService,
            completionService,
            context,
            budgetDecisionService,
            logger);
}

// 并发契约（F-11）：本类依赖 Workflow 引擎对 Executor 的串行调度保证——同一 GoalRun 的
// Plan/ExecuteNext/Complete 永不并发执行。_run 仅是 fenced 持久化副本的内存缓存，所有写路径
// 都经 SaveFencedAsync(fencing token) 兜底，跨世代（fencing token 变化）的脏写会被拒绝。
internal sealed class GoalWorkflowRuntime(
    IGoalPlanningService decomposer,
    IGoalStepExecutionService subGoalExecutor,
    IGoalRunStore goalRunStore,
    IGoalWorkspaceService workspaceService,
    IGoalCompletionService completionService,
    GoalWorkflowRuntimeContext context,
    IBudgetDecisionService budgetDecisionService,
    ILogger<GoalWorkflowRuntime>? logger = null) : IGoalWorkflowRuntime
{
    private const int MaxRecursiveDecompositionDepth = 3;
    private const int ExtraAttemptsPerExtension = 10;
    private const long ExtraTokensPerExtension = 100_000;
    private static readonly TimeSpan ExtraWallClockPerExtension = TimeSpan.FromHours(1);

    private GoalBudget _budget = context.Options.Budget ?? new GoalBudget();
    private readonly GoalRuntimeProgress _progress = new(context.EventWriter, context.Options.Budget ?? new GoalBudget());
    private GoalRun? _run;
    private long _fencingToken;

    public async Task BindAsync(GoalRun run, long fencingToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (run.WorkflowFencingToken != fencingToken || fencingToken <= 0)
            throw new InvalidOperationException("Goal runtime received a stale fencing token.");
        // B4：每次世代绑定把活动时间锚定为 now——Paused/进程离线区间不计入墙钟
        // （否则 resume 后第一次 RollForwardWallClock 会把整段离线时间一次性计入，
        // 立即再次触发 ShouldForceTerminate，形成「resume 即暂停」空转）。
        if (run.Budget.LastActivityAt is not null)
        {
            run = run with { Budget = run.Budget with { LastActivityAt = DateTimeOffset.UtcNow } };
            await goalRunStore.SaveFencedAsync(run, run.Version, fencingToken, ct).ConfigureAwait(false);
        }
        _run = run;
        _fencingToken = fencingToken;
    }

    public async Task<GoalWorkflowState> PlanAsync(GoalWorkflowInput input, CancellationToken ct)
    {
        var run = RequireBoundRun(input.GoalRunId);
        if (run.Plan.Count > 0)
        {
            if (run.State == GoalRunState.Paused)
            {
                var resumed = run with
                {
                    State = GoalRunState.Executing,
                    TerminalReason = null,
                    FailureSummary = null,
                };
                await SaveAsync(resumed, ct).ConfigureAwait(false);
                run = _run!;
            }
            return ToWorkflowState(run, FindNextIndex(run.Plan), run.HasReplanned);
        }

        var result = await decomposer.DecomposeWithFallbackAsync(
            input.Goal,
            input.ModelId,
            ct).ConfigureAwait(false);
        var plan = result.Plan.Goals.Select(ToSnapshot).ToArray();
        var budget = run.Budget with
        {
            TotalInputTokens = run.Budget.TotalInputTokens + result.InputTokens,
            TotalOutputTokens = run.Budget.TotalOutputTokens + result.OutputTokens,
        };
        if (result.UsedFallback)
        {
            // H2: 分解失败回退为单目标继续执行（与 DecomposeWithFallbackAsync 的设计语义一致，
            // 等价于 Build 模式"不分解直接执行"）。预算、迭代上限与硬验证门仍然生效；
            // 分解失败原因已由 Decomposer LogWarning 留痕。
            var fallback = run with
            {
                Plan = plan,
                Budget = budget,
                State = GoalRunState.Executing,
                FailureSummary = null,
            };
            await SaveAsync(fallback, ct).ConfigureAwait(false);
            _progress.PlanCreated(plan);
            return ToWorkflowState(_run!, currentIndex: 0, hasReplanned: _run!.HasReplanned);
        }

        var updated = run with
        {
            Plan = plan,
            Budget = budget,
            State = plan.Length == 0 ? GoalRunState.Failed : GoalRunState.Executing,
            FailureSummary = plan.Length == 0 ? result.Error ?? "Goal decomposition produced no executable steps." : null,
        };
        await SaveAsync(updated, ct).ConfigureAwait(false);
        _progress.PlanCreated(plan);
        return ToWorkflowState(_run!, currentIndex: 0, hasReplanned: _run!.HasReplanned);
    }

    public async Task<GoalWorkflowState> ExecuteNextAsync(GoalWorkflowState state, CancellationToken ct)
    {
        var run = RequireBoundRun(state.GoalRunId);
        if (state.CurrentIndex < 0 || state.CurrentIndex >= state.Plan.Count)
            throw new InvalidOperationException("Goal workflow cursor is outside the current plan.");

        // Fix-7：墙钟只累计运行区间（Paused / 进程离线时间不计入），并回写到本次状态流转。
        var budget = GoalBudgetAccountant.RollForwardWallClock(run.Budget);
        state = state with { Budget = budget };
        var usage = GoalBudgetAccountant.BuildUsage(budget);
        // Fix-6：EvaluateWarning 已存在但从未发布——级别变化时推送 TUI 预警（黄/橙）。
        _progress.PublishBudgetWarning(usage);
        var step = state.Plan[state.CurrentIndex];

        while (_budget.ShouldForceTerminate(usage)
            || (_budget.MaxSubGoalAttempts - state.Budget.TotalAttempts < GoalLoopDefaults.MaxAttemptsPerSubGoal))
        {
            var isAttemptShortfall = !_budget.ShouldForceTerminate(usage);
            var reason = isAttemptShortfall
                ? $"剩余尝试额度（{Math.Max(0, _budget.MaxSubGoalAttempts - state.Budget.TotalAttempts)} 次）不足以安全执行下一个子目标（需至少 {GoalLoopDefaults.MaxAttemptsPerSubGoal} 次）。"
                : $"Goal 执行预算已耗尽（尝试 {usage.TotalAttempts}/{_budget.MaxSubGoalAttempts} 次，Token {usage.TotalTokens:N0}{(_budget.MaxTotalTokens is { } maxTok ? $"/{maxTok:N0}" : "")}）。";

            var decision = await PromptBudgetDecisionAsync(reason, usage, step.Description, ct).ConfigureAwait(false);
            if (decision == GoalBudgetDecision.ContinueWithExtraBudget)
            {
                ExtendBudget(usage);
                _progress.Progress("已追加预算额度，继续执行");
                continue;
            }

            if (decision == GoalBudgetDecision.SkipCurrentStep)
            {
                if (state.State == GoalRunState.Paused)
                    state = state with { State = GoalRunState.Executing, FailureSummary = null };

                var skippedEvidence = new GoalStepExecutionEvidence(
                    step.Id,
                    GoalStepState.Skipped,
                    Attempts: 0,
                    InputTokens: 0,
                    OutputTokens: 0,
                    AgentOutput: string.Empty,
                    Evaluation: "Skipped by user after budget limit reached.",
                    ChangedFiles: [],
                    ToolExecutions: [],
                    Validations: [new GoalGateEvidence("budget", false, true, "Skipped by user after budget limit reached.")],
                    Diagnostics: []);
                var skipReceipt = await workspaceService.RecordStepAsync(
                    run,
                    skippedEvidence,
                    _fencingToken,
                    ct).ConfigureAwait(false);
                _progress.Progress($"已按用户选择跳过子目标 #{step.Id}");
                return await ApplyEvidenceAsync(state, skipReceipt.Evidence, ct).ConfigureAwait(false);
            }

            if (decision == GoalBudgetDecision.Abort)
            {
                var cancelled = run with
                {
                    Budget = budget,
                    State = GoalRunState.Cancelled,
                    TerminalReason = OneCode.Core.Workflows.RunTerminalReason.Cancelled,
                    FailureSummary = "Goal execution aborted by user at budget prompt.",
                };
                await SaveAsync(cancelled, ct).ConfigureAwait(false);
                _progress.Progress("已终止 Goal 执行");
                return ToWorkflowState(_run!, state.CurrentIndex, state.HasReplanned);
            }

            var paused = run with
            {
                Budget = budget,
                State = GoalRunState.Paused,
                TerminalReason = OneCode.Core.Workflows.RunTerminalReason.BudgetExceeded,
                FailureSummary = isAttemptShortfall
                    ? "剩余尝试额度不足以安全执行下一个子目标，已暂停（追加预算后 /resume 继续）。"
                    : "Goal execution budget was exhausted.",
            };
            await SaveAsync(paused, ct).ConfigureAwait(false);
            _progress.Progress(isAttemptShortfall
                ? "剩余尝试额度不足，已暂停（追加预算后 /resume 继续）"
                : "预算耗尽，已暂停（可通过 /resume 恢复）");
            return ToWorkflowState(_run!, state.CurrentIndex, state.HasReplanned);
        }

        if (state.State == GoalRunState.Paused)
            state = state with { State = GoalRunState.Executing, FailureSummary = null };

        var existingReceipt = await workspaceService.FindStepReceiptAsync(
            run,
            step.Id,
            _fencingToken,
            ct).ConfigureAwait(false);
        if (existingReceipt is not null)
            return await ApplyEvidenceAsync(state, existingReceipt.Evidence, ct).ConfigureAwait(false);

        if (step.NeedsFurtherDecomposition)
        {
            if (step.Depth < MaxRecursiveDecompositionDepth)
            {
                var parent = ToGoalItem(step);
                var nextId = state.Plan.Count == 0 ? 1 : state.Plan.Max(item => item.Id) + 1;
                var decomposition = await decomposer.DecomposeSubGoalAsync(
                    parent,
                    nextId,
                    context.Options.ModelId,
                    ct).ConfigureAwait(false);
                if (decomposition is { SubGoals.Count: > 0 })
                {
                    var expanded = state.Plan.ToList();
                    expanded[state.CurrentIndex] = step with { State = GoalStepState.Skipped };
                    expanded.InsertRange(state.CurrentIndex + 1, decomposition.Value.SubGoals.Select(ToSnapshot));
                    var expandedBudget = GoalBudgetAccountant
                        .AddLlmUsage(state.Budget, decomposition.Value.InputTokens, decomposition.Value.OutputTokens);
                    var expandedState = state with
                    {
                        Plan = expanded,
                        Budget = expandedBudget,
                        CurrentIndex = state.CurrentIndex + 1,
                    };
                    await SaveStateAsync(expandedState, ct).ConfigureAwait(false);
                    return expandedState;
                }
                // Fix-5/F-06：子目标分解失败回退直执行必须留痕，不得静默降级。
                logger?.LogWarning(
                    "subgoal decompose fallback, step #{Id}: decomposition returned no sub-goals; executing undecomposed.",
                    step.Id);
            }
            else
            {
                // Fix-5/F-05：达到递归拆分深度上限后不得静默执行欠拆分目标。
                logger?.LogWarning(
                    "goal step #{Id} reached max recursive decomposition depth ({Depth}); executing without further decomposition.",
                    step.Id,
                    MaxRecursiveDecompositionDepth);
            }
            step = step with { DecompositionFallback = true };
        }

        var currentGoal = ToGoalItem(step with { State = GoalStepState.InProgress });
        // P0：步骤开始进度（含预算快照）——消除 Goal 模式执行期的长时间静默。
        _progress.StepStarted(state.CurrentIndex, state.Plan.Count, step, GoalBudgetAccountant.BuildUsage(state.Budget), state.Plan, state.Executions);
        var currentPlan = new GoalPlan { Goals = state.Plan.Select(ToGoalItem).ToArray() };
        var priorExecutions = state.Executions.Select(ToSubGoalExecution).ToArray();
        subGoalExecutor.UpdateGoalContext(
            currentPlan,
            currentGoal,
            priorExecutions);

        using var transaction = context.TransactionFactory();

        SubGoalExecution execution;
        try
        {
            if (run.Workspace is null)
                throw new InvalidOperationException("GoalRun has no isolated workspace.");

            var effectiveWorktreeDir = GoalWorkspacePathResolver.ResolveEffectiveWorkingDirectory(run);
            var options = context.Options with
            {
                WorkingDirectory = effectiveWorktreeDir,
            };
            execution = await subGoalExecutor.ExecuteSubGoalWithLoopStreamingAsync(
                currentGoal,
                options,
                transaction,
                context.EventWriter,
                ct).ConfigureAwait(false);
            // Goal 模式运行在独立的隔离 worktree 中（主仓库不受影响），
            // 取消或步骤失败时不再调用 transaction.Rollback() 删除 worktree 中的阶段性文件，
            // 以便 Replan 或 /resume 在已有进展上继续。
            // 单代理步骤失败时回执清空 ChangedFiles，防止被 replan 取代的失败步触发收尾 change-scope 误判。
            var isDagStep = step.SubTasks is { Count: > 0 };
            var clearFailedStepChangedFiles = !isDagStep && execution.Status != GoalStatus.Completed;
            if (step.DecompositionFallback)
                execution = execution with { Evaluation = $"{execution.Evaluation} [decomposition-fallback]" };
            if (isDagStep && execution.Status != GoalStatus.Completed)
                execution = execution with { Evaluation = $"{execution.Evaluation} [partial-committed]" };
            var evidence = ToEvidence(execution, transaction.GetModifiedFiles(), clearFailedStepChangedFiles);
            GoalStepReceipt receipt;
            try
            {
                receipt = await workspaceService.RecordStepAsync(
                    run,
                    evidence,
                    _fencingToken,
                    ct).ConfigureAwait(false);
            }
            catch (Exception receiptEx)
            {
                var reconciled = await workspaceService.FindStepReceiptAsync(
                    run,
                    evidence.GoalId,
                    _fencingToken,
                    CancellationToken.None).ConfigureAwait(false);
                if (reconciled is null)
                {
                    transaction.PreserveForManualReconciliation();
                    ExceptionDispatchInfo.Capture(receiptEx).Throw();
                }
                receipt = reconciled;
            }

            transaction.Commit();
            var applied = await ApplyEvidenceAsync(state, receipt.Evidence, ct).ConfigureAwait(false);
            if (receipt.Evidence.State == GoalStepState.Failed
                && applied.CurrentIndex < applied.Plan.Count
                && !applied.HasReplanned)
            {
                var replan = await decomposer.ReplanAsync(
                    run.Goal,
                    new GoalPlan { Goals = applied.Plan.Select(ToGoalItem).ToArray() },
                    state.CurrentIndex,
                    applied.Executions.Select(ToSubGoalExecution).ToArray(),
                    context.Options.ModelId,
                    ct).ConfigureAwait(false);
                if (replan is { RemainingGoals.Count: > 0 })
                {
                    // 失败步保留在 plan 中（聚合不变式：回执引用的步骤必须存在于 plan），
                    // 标记 Skipped + SupersededByReplan——完成完整性检查与聚合校验据此豁免。
                    // 不标记的话 replan 后的 run 终态必为 Failed，replan 形同虚设。
                    // replan 语义：替换失败步之后的全部剩余步骤；游标指向首个 replanned 步。
                    var failedIndex = applied.CurrentIndex - 1;
                    var retained = applied.Plan.Take(failedIndex + 1).ToList();
                    retained[failedIndex] = retained[failedIndex] with
                    {
                        State = GoalStepState.Skipped,
                        SupersededByReplan = true,
                    };
                    retained.AddRange(replan.Value.RemainingGoals.Select(ToSnapshot));
                    applied = applied with
                    {
                        Plan = retained,
                        HasReplanned = true,
                        Budget = applied.Budget with
                        {
                            TotalInputTokens = applied.Budget.TotalInputTokens + replan.Value.InputTokens,
                            TotalOutputTokens = applied.Budget.TotalOutputTokens + replan.Value.OutputTokens,
                        },
                    };
                    await SaveStateAsync(applied, ct).ConfigureAwait(false);
                    // P3：replan 对用户可见——失败步骤的后续安排是关键节点，且立即刷新底部计划面板。
                    _progress.PlanReplanned(
                        applied.Plan,
                        applied.Executions,
                        applied.CurrentIndex,
                        GoalBudgetAccountant.BuildUsage(applied.Budget));
                }
            }
            return applied;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an interruption. Keep the GoalRun Executing and preserve
            // worktree files so the next workflow generation (/resume) can continue.
            transaction.PreserveForManualReconciliation();
            throw;
        }
        catch (Exception ex)
        {
            await SaveTerminalAsync(
                GoalRunState.Failed,
                OneCode.Core.Workflows.RunTerminalReason.AgentException,
                ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GoalBudgetDecision> PromptBudgetDecisionAsync(
        string reason,
        GoalBudgetUsage usage,
        string stepDescription,
        CancellationToken ct)
    {
        // 后台/无头运行不打扰用户，直接暂停（与未开启预算提示时的既有语义一致）。
        if (!context.PromptOnBudgetExhausted)
            return GoalBudgetDecision.Pause;

        return await budgetDecisionService.DecideAsync(
            new BudgetExhaustedContext(reason, usage, _budget, stepDescription),
            ct).ConfigureAwait(false);
    }

    private void ExtendBudget(GoalBudgetUsage usage)
    {
        var minRequiredAttempts = usage.TotalAttempts + GoalLoopDefaults.MaxAttemptsPerSubGoal;
        var nextAttempts = Math.Max(_budget.MaxSubGoalAttempts + ExtraAttemptsPerExtension, minRequiredAttempts);
        long? nextTokens = _budget.MaxTotalTokens is { } maxTok
            ? Math.Max(maxTok + ExtraTokensPerExtension, usage.TotalTokens + ExtraTokensPerExtension)
            : null;
        TimeSpan? nextWallClock = _budget.MaxWallClock is { } maxClock
            ? (usage.Elapsed is { } elapsed && elapsed >= maxClock
                ? elapsed + ExtraWallClockPerExtension
                : maxClock + ExtraWallClockPerExtension)
            : null;

        _budget = _budget with
        {
            MaxSubGoalAttempts = nextAttempts,
            MaxTotalTokens = nextTokens,
            MaxWallClock = nextWallClock,
        };
        _progress.UpdateBudget(_budget);
    }

    public async Task<GoalWorkflowOutput> CompleteAsync(GoalWorkflowState state, CancellationToken ct)
    {
        var run = RequireBoundRun(state.GoalRunId);
        if (run.State is GoalRunState.Executing or GoalRunState.Validating or GoalRunState.Publishing)
        {
            run = await completionService.CompleteAsync(run, _fencingToken, ct).ConfigureAwait(false);
            _run = run;
        }
        return new GoalWorkflowOutput(
            run.Id,
            run.State,
            run.Plan.Count(step => step.State == GoalStepState.Completed),
            run.Plan.Count(step => step.State == GoalStepState.Failed),
            run.FailureSummary);
    }

    private async Task<GoalWorkflowState> ApplyEvidenceAsync(
        GoalWorkflowState state,
        GoalStepExecutionEvidence evidence,
        CancellationToken ct)
    {
        var plan = state.Plan.ToList();
        var index = plan.FindIndex(step => step.Id == evidence.GoalId);
        if (index < 0)
            throw new InvalidOperationException($"Goal step '{evidence.GoalId}' is not present in the workflow plan.");
        plan[index] = plan[index] with { State = evidence.State };
        var previous = state.Executions.LastOrDefault(item => item.GoalId == evidence.GoalId);
        var executions = state.Executions
            .Where(item => item.GoalId != evidence.GoalId)
            .Append(evidence)
            .ToArray();
        var budget = GoalBudgetAccountant.AccumulateEvidence(state.Budget, previous, evidence);
        var next = state with
        {
            Plan = plan,
            Executions = executions,
            Budget = budget,
            CurrentIndex = index + 1,
        };
        await SaveStateAsync(next, ct).ConfigureAwait(false);
        // P3：步骤回执对用户可见——✓/✗/跳过 + 重试轮数 + 变更文件数。
        _progress.StepReceipt(index, state.Plan.Count, evidence, next.Plan, next.Executions, GoalBudgetAccountant.BuildUsage(next.Budget));
        return next;
    }

    private async Task SaveStateAsync(GoalWorkflowState state, CancellationToken ct)
    {
        var run = RequireBoundRun(state.GoalRunId);
        await SaveAsync(run with
        {
            Plan = state.Plan,
            Executions = state.Executions,
            Budget = state.Budget,
            HasReplanned = state.HasReplanned,
            State = state.State,
            FailureSummary = state.FailureSummary,
        }, ct).ConfigureAwait(false);
    }

    private async Task SaveTerminalAsync(
        GoalRunState state,
        OneCode.Core.Workflows.RunTerminalReason terminalReason,
        string failureSummary)
    {
        var current = _run ?? throw new InvalidOperationException("Goal runtime was not bound.");
        await SaveAsync(current with
        {
            State = state,
            TerminalReason = terminalReason,
            FailureSummary = failureSummary,
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SaveAsync(GoalRun candidate, CancellationToken ct)
    {
        var current = _run ?? throw new InvalidOperationException("Goal runtime was not bound.");
        await goalRunStore.SaveFencedAsync(candidate, current.Version, _fencingToken, ct).ConfigureAwait(false);
        _run = await goalRunStore.LoadByIdAsync(current.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GoalRun '{current.Id}' disappeared after a fenced save.");
    }

    private GoalRun RequireBoundRun(GoalRunId runId)
    {
        var run = _run ?? throw new InvalidOperationException("Goal runtime was not bound to a durable run.");
        if (run.Id != runId || run.WorkflowFencingToken != _fencingToken)
            throw new InvalidOperationException("Goal runtime binding does not match the workflow input.");
        return run;
    }
}
