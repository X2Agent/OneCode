using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OneCode.App.Services.Agent;
using OneCode.App.Tui;
using OneCode.Core.Cost;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.GoalMode;

public sealed record GoalWorkflowRuntimeContext(
    GoalRunOptions Options,
    ChannelWriter<TuiEvent> EventWriter,
    Func<EditTransaction> TransactionFactory,
    OneCode.Core.Workflows.IOperationLedger? Ledger = null);

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
    ICostTracker costTracker,
    OneCode.Core.Workflows.IOperationLedger? operationLedger = null,
    ILogger<GoalWorkflowRuntime>? logger = null) : IGoalWorkflowRuntimeFactory
{
    public IGoalWorkflowRuntime Create(GoalWorkflowRuntimeContext context)
        => new GoalWorkflowRuntime(
            planningService,
            stepExecutionService,
            goalRunStore,
            workspaceService,
            completionService,
            costTracker,
            context with { Ledger = context.Ledger ?? operationLedger },
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
    ICostTracker costTracker,
    GoalWorkflowRuntimeContext context,
    ILogger<GoalWorkflowRuntime>? logger = null) : IGoalWorkflowRuntime
{
    private const int MaxRecursiveDecompositionDepth = 3;
    private readonly GoalBudget _budget = context.Options.Budget ?? new GoalBudget();
    private GoalRun? _run;
    private long _fencingToken;
    private GoalBudgetWarningLevel? _lastWarningLevel;

    public async Task BindAsync(GoalRun run, long fencingToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (run.WorkflowFencingToken != fencingToken || fencingToken <= 0)
            throw new InvalidOperationException("Goal runtime received a stale fencing token.");
        _run = run;
        _fencingToken = fencingToken;
        // Fix-2/N-02：成本基线只允许建立一次并随 Run 持久化。
        // - 首次 Bind：基线 = 当前进程累计成本（此时 EstimatedCostUsd 尚未入账，差值恒等）。
        // - resume 重新 Bind：CostBaselineUsd 已持久化，直接沿用，禁止二次减 EstimatedCostUsd。
        // - 旧版本快照（无 CostBaselineUsd 字段）：按旧公式换算一次后持久化，后续走快照路径。
        if (run.Budget.CostBaselineUsd == 0m)
        {
            var baseline = Math.Max(0m, costTracker.GetTotalCost() - run.Budget.EstimatedCostUsd);
            await SaveAsync(run with { Budget = run.Budget with { CostBaselineUsd = baseline } }, ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<GoalWorkflowState> PlanAsync(GoalWorkflowInput input, CancellationToken ct)
    {
        var run = RequireBoundRun(input.GoalRunId);
        if (run.Plan.Count > 0)
            return ToWorkflowState(run, FindNextIndex(run.Plan), run.HasReplanned);

        var result = await decomposer.DecomposeWithFallbackAsync(
            input.Goal,
            input.ModelId,
            ct).ConfigureAwait(false);
        var plan = result.Plan.Goals.Select(ToSnapshot).ToArray();
        var budget = run.Budget with
        {
            TotalInputTokens = run.Budget.TotalInputTokens + result.InputTokens,
            TotalOutputTokens = run.Budget.TotalOutputTokens + result.OutputTokens,
            EstimatedCostUsd = CurrentExecutionCost(),
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
        return ToWorkflowState(_run!, currentIndex: 0, hasReplanned: _run!.HasReplanned);
    }

    public async Task<GoalWorkflowState> ExecuteNextAsync(GoalWorkflowState state, CancellationToken ct)
    {
        var run = RequireBoundRun(state.GoalRunId);
        if (state.CurrentIndex < 0 || state.CurrentIndex >= state.Plan.Count)
            throw new InvalidOperationException("Goal workflow cursor is outside the current plan.");

        // Fix-7：墙钟只累计运行区间（Paused / 进程离线时间不计入），并回写到本次状态流转。
        var budget = RollForwardWallClock(run.Budget);
        state = state with { Budget = budget };
        var usage = BuildBudgetUsage(budget);
        // Fix-6：EvaluateWarning 已存在但从未发布——级别变化时推送 TUI 预警（黄/橙）。
        PublishBudgetWarning(usage);
        if (_budget.ShouldForceTerminate(usage))
        {
            var paused = run with
            {
                Budget = budget,
                State = GoalRunState.Paused,
                TerminalReason = OneCode.Core.Build.BuildTerminalReason.BudgetExceeded,
                FailureSummary = "Goal execution budget was exhausted.",
            };
            await SaveAsync(paused, ct).ConfigureAwait(false);
            return ToWorkflowState(_run!, state.CurrentIndex, state.HasReplanned);
        }

        var step = state.Plan[state.CurrentIndex];
        // Fix-7/N-01：守卫不依赖游标位置——resume 到 index 0 时同样受剩余尝试额度约束。
        if (_budget.MaxSubGoalAttempts - state.Budget.TotalAttempts < GoalSubGoalExecutor.MaxAttemptsPerSubGoal)
        {
            var skippedEvidence = new GoalStepExecutionEvidence(
                step.Id,
                GoalStepState.Skipped,
                0,
                0,
                0,
                string.Empty,
                "Skipped because the remaining attempt budget cannot safely execute another sub-goal.",
                [],
                [],
                [new GoalGateEvidence("budget", false, true, "Insufficient remaining attempt budget.")],
                []);
            var skippedReceipt = await workspaceService.RecordStepAsync(
                run,
                skippedEvidence,
                _fencingToken,
                ct).ConfigureAwait(false);
            return await ApplyEvidenceAsync(state, skippedReceipt.Evidence, ct).ConfigureAwait(false);
        }

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
                    var expandedBudget = state.Budget with
                    {
                        TotalInputTokens = state.Budget.TotalInputTokens + decomposition.Value.InputTokens,
                        TotalOutputTokens = state.Budget.TotalOutputTokens + decomposition.Value.OutputTokens,
                        EstimatedCostUsd = CurrentExecutionCost(),
                    };
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
        var currentPlan = new GoalPlan { Goals = state.Plan.Select(ToGoalItem).ToArray() };
        var priorExecutions = state.Executions.Select(ToSubGoalExecution).ToArray();
        subGoalExecutor.UpdateGoalContext(
            currentPlan,
            currentGoal,
            priorExecutions,
            sharedTransactionOwned: false);

        using var transaction = context.TransactionFactory();
        // S-04: 持久化 Operation Ledger——新世代前回滚本 Run 上一世代残留，开启本 step 的事务。
        var ledger = context.Ledger;
        var stepOperationId = $"goal/{run.Id}/step/{step.Id}/fence/{_fencingToken}";
        if (ledger is not null)
        {
            await ledger.ReconcileRunAsync($"goal/{run.Id}", ct).ConfigureAwait(false);
            await ledger.BeginTransactionAsync(
                stepOperationId,
                "file-transaction",
                _fencingToken,
                ct).ConfigureAwait(false);
            transaction.PersistTo(ledger, stepOperationId, _fencingToken);
        }

        SubGoalExecution execution;
        try
        {
            var options = context.Options with
            {
                WorkingDirectory = run.Workspace?.IsolatedPath
                    ?? throw new InvalidOperationException("GoalRun has no isolated workspace."),
                SharedTransaction = transaction,
            };
            execution = await subGoalExecutor.ExecuteSubGoalWithLoopStreamingAsync(
                currentGoal,
                options,
                transaction,
                context.EventWriter,
                ct).ConfigureAwait(false);
            // H4: 失败路径先回滚再构造证据——回执不得记录已回滚的文件，否则
            // GoalCompletionService 的 change-scope 门禁会按"声称改过、实际已还原"的
            // 路径误杀整 run。
            var rolledBack = execution.Status != GoalStatus.Completed;
            if (rolledBack)
                transaction.Rollback();
            if (step.DecompositionFallback)
                execution = execution with { Evaluation = $"{execution.Evaluation} [decomposition-fallback]" };
            var evidence = ToEvidence(execution, transaction.GetModifiedFiles(), rolledBack);
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
                    // Fix-8/F-12：rollback 次生异常不得吞掉原始异常。
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception rollbackEx)
                    {
                        logger?.LogError(
                            rollbackEx,
                            "Rollback failed after step receipt failure for goal step #{Id}.",
                            evidence.GoalId);
                    }
                    ExceptionDispatchInfo.Capture(receiptEx).Throw();
                }
                receipt = reconciled;
            }
            // S-04: 先持久化提交（ledger receipt）再内存提交——防止"内存已提交、ledger 未提交"崩溃后误回滚。
            if (ledger is not null)
            {
                await ledger.CommitTransactionAsync(
                    stepOperationId,
                    _fencingToken,
                    $"goal-step:{run.Id}:{step.Id}:{_fencingToken}",
                    ct).ConfigureAwait(false);
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
                    var retained = applied.Plan.Take(applied.CurrentIndex).ToList();
                    retained.AddRange(replan.Value.RemainingGoals.Select(ToSnapshot));
                    applied = applied with
                    {
                        Plan = retained,
                        HasReplanned = true,
                        Budget = applied.Budget with
                        {
                            TotalInputTokens = applied.Budget.TotalInputTokens + replan.Value.InputTokens,
                            TotalOutputTokens = applied.Budget.TotalOutputTokens + replan.Value.OutputTokens,
                            EstimatedCostUsd = CurrentExecutionCost(),
                        },
                    };
                    await SaveStateAsync(applied, ct).ConfigureAwait(false);
                }
            }
            return applied;
        }
        catch (OperationCanceledException)
        {
            SafeRollback(transaction);
            // Cancellation is an interruption. Keep the GoalRun Executing so the
            // next workflow generation can reacquire it and retry the unfinished step.
            throw;
        }
        catch (Exception ex)
        {
            SafeRollback(transaction);
            await SaveTerminalAsync(
                GoalRunState.Failed,
                OneCode.Core.Build.BuildTerminalReason.AgentException,
                ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    private void SafeRollback(EditTransaction transaction)
    {
        // Fix-8：终态异常路径中 rollback 失败只记录次生异常，保留原始异常向上传播。
        try
        {
            transaction.Rollback();
        }
        catch (Exception rollbackEx)
        {
            logger?.LogError(rollbackEx, "Goal step transaction rollback failed; original exception is preserved.");
        }
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
        var budget = state.Budget with
        {
            // Fix-2/F-02：差值公式加下限保护——budget-skip 等场景下新证据计数为 0 时，
            // 不得对旧证据做负扣减导致预算消耗回退。
            TotalAttempts = state.Budget.TotalAttempts + Math.Max(0, evidence.Attempts - (previous?.Attempts ?? 0)),
            TotalInputTokens = state.Budget.TotalInputTokens + Math.Max(0, evidence.InputTokens - (previous?.InputTokens ?? 0)),
            TotalOutputTokens = state.Budget.TotalOutputTokens + Math.Max(0, evidence.OutputTokens - (previous?.OutputTokens ?? 0)),
            EstimatedCostUsd = CurrentExecutionCost(),
        };
        var next = state with
        {
            Plan = plan,
            Executions = executions,
            Budget = budget,
            CurrentIndex = index + 1,
        };
        await SaveStateAsync(next, ct).ConfigureAwait(false);
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
        OneCode.Core.Build.BuildTerminalReason terminalReason,
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

    private decimal CurrentExecutionCost()
        // Fix-2：以持久化的 CostBaselineUsd 快照为基线，不再依赖实例字段（resume 安全）。
        => Math.Max(0m, costTracker.GetTotalCost() - (_run?.Budget.CostBaselineUsd ?? 0m));

    private static GoalBudgetSnapshot RollForwardWallClock(GoalBudgetSnapshot budget)
    {
        // Fix-7：墙钟语义 = 仅累计运行区间。LastActivityAt 是上次活动时间戳，
        // Paused / 进程离线期间的时间不计入；首次调用只打点不累计。
        var now = DateTimeOffset.UtcNow;
        if (budget.LastActivityAt is not { } last)
            return budget with { LastActivityAt = now };
        return budget with
        {
            AccumulatedElapsed = budget.AccumulatedElapsed + (now - last),
            LastActivityAt = now,
        };
    }

    private static GoalBudgetUsage BuildBudgetUsage(GoalBudgetSnapshot budget)
        => new(
            budget.TotalAttempts,
            budget.TotalInputTokens + budget.TotalOutputTokens,
            ResolveElapsed(budget),
            budget.EstimatedCostUsd);

    private static TimeSpan? ResolveElapsed(GoalBudgetSnapshot budget)
        // 旧版本快照兼容：无累加墙钟时回退到"自 StartedAt 起的总墙钟"。
        => budget.LastActivityAt is null && budget.AccumulatedElapsed == TimeSpan.Zero
            ? DateTimeOffset.UtcNow - budget.StartedAt
            : budget.AccumulatedElapsed;

    private void PublishBudgetWarning(GoalBudgetUsage usage)
    {
        var level = _budget.EvaluateWarning(usage);
        if (level == _lastWarningLevel)
            return;
        _lastWarningLevel = level;
        if (level is null)
            return;
        context.EventWriter.TryWrite(new TuiGoalBudgetWarning(
            level.Value,
            usage.TotalAttempts,
            usage.TotalTokens,
            usage.Elapsed,
            usage.EstimatedCostUsd));
    }

    private static int FindNextIndex(IReadOnlyList<GoalStepSnapshot> plan)
    {
        for (var index = 0; index < plan.Count; index++)
        {
            if (plan[index].State is GoalStepState.Pending or GoalStepState.InProgress)
                return index;
        }
        return plan.Count;
    }

    private static GoalWorkflowState ToWorkflowState(GoalRun run, int currentIndex, bool hasReplanned)
        => new(
            run.Id,
            run.Plan,
            run.Executions,
            run.Budget,
            currentIndex,
            hasReplanned,
            run.State,
            run.FailureSummary);

    private static GoalStepSnapshot ToSnapshot(GoalItem item)
        => new(
            item.Id,
            item.Description,
            item.SuccessCriteria,
            ToStepState(item.Status),
            item.RequiredTools ?? [],
            item.Depth,
            item.NeedsFurtherDecomposition,
            item.ExpectedFiles,
            item.AllowedPaths,
            item.RequiresBuild,
            item.RequiresTests,
            item.Optional);

    private static GoalItem ToGoalItem(GoalStepSnapshot item)
        => new()
        {
            Id = item.Id,
            Description = item.Description,
            SuccessCriteria = item.SuccessCriteria,
            Status = ToGoalStatus(item.State),
            RequiredTools = item.RequiredTools,
            Depth = item.Depth,
            NeedsFurtherDecomposition = item.NeedsFurtherDecomposition,
            ExpectedFiles = item.ExpectedFiles,
            AllowedPaths = item.AllowedPaths,
            RequiresBuild = item.RequiresBuild,
            RequiresTests = item.RequiresTests,
            Optional = item.Optional,
        };

    private static GoalStepExecutionEvidence ToEvidence(
        SubGoalExecution execution,
        IReadOnlyList<string> fallbackChangedFiles,
        bool rolledBack = false)
    {
        var source = execution.Evidence;
        // rolledBack: 文件改动已整体还原，即使内部 agent 证据带文件列表，回执也记录为空。
        return new GoalStepExecutionEvidence(
            execution.GoalId,
            ToStepState(execution.Status),
            execution.Attempts,
            execution.InputTokens,
            execution.OutputTokens,
            execution.AgentOutput,
            execution.Evaluation,
            rolledBack ? [] : source?.ChangedFiles ?? fallbackChangedFiles,
            source?.ToolExecutions.Select(item => new GoalToolEvidence(item.ToolName, item.IsError, item.Result)).ToArray() ?? [],
            source?.Validations.Select(item => new GoalGateEvidence(item.Gate, item.Passed, item.Skipped, item.Summary)).ToArray() ?? [],
            source?.Diagnostics ?? []);
    }

    private static SubGoalExecution ToSubGoalExecution(GoalStepExecutionEvidence evidence)
        => new(
            evidence.GoalId,
            ToGoalStatus(evidence.State),
            evidence.Attempts,
            evidence.InputTokens,
            evidence.OutputTokens,
            evidence.AgentOutput,
            evidence.Evaluation,
            new SubGoalEvidence(
                evidence.AgentOutput,
                evidence.ChangedFiles,
                evidence.ToolExecutions.Select(item => new GoalToolExecutionEvidence(item.ToolName, item.IsError, item.Result)).ToArray(),
                evidence.Validations.Select(item => new GoalValidationEvidence(item.Gate, item.Passed, item.Skipped, item.Summary)).ToArray(),
                evidence.Diagnostics));

    private static GoalStepState ToStepState(GoalStatus status)
        => status switch
        {
            GoalStatus.Pending => GoalStepState.Pending,
            GoalStatus.InProgress => GoalStepState.InProgress,
            GoalStatus.Completed => GoalStepState.Completed,
            GoalStatus.Failed => GoalStepState.Failed,
            GoalStatus.Skipped => GoalStepState.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static GoalStatus ToGoalStatus(GoalStepState state)
        => state switch
        {
            GoalStepState.Pending => GoalStatus.Pending,
            GoalStepState.InProgress => GoalStatus.InProgress,
            GoalStepState.Completed => GoalStatus.Completed,
            GoalStepState.Failed => GoalStatus.Failed,
            GoalStepState.Skipped => GoalStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
}
