using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Query;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Goals;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;
using OneCode.Core.Session;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// Bridges GOAL orchestration backend to the TUI streaming pipeline.
/// GOAL mode runs through the durable MAF pipeline
/// (<see cref="GoalMode.GoalWorkflowHost"/>), converting
/// <see cref="OrchestrationEvent"/> streams into <see cref="QueryEvent"/> streams.
/// </summary>
public sealed class OrchestrationStreamService(
    IConfigManager configManager,
    IToolCatalog toolCatalog,
    IToolCapabilityResolver toolCapabilityResolver,
    IWorkingDirectoryAccessor wd,
    IGoalRunApplicationService goalRunApplicationService,
    GoalWorkflowHost goalWorkflowHost,
    IGoalWorkflowRuntimeFactory goalRuntimeFactory,
    ITokenLedger tokenLedger)
{
    /// <summary>
    /// GOAL 工具面经能力门解析（<see cref="IToolCapabilityResolver"/>）：
    /// 全量可见工具 − PlanExclusive（SubmitPlan），与 BUILD/PLAN 共用同一真相源——
    /// 每种模式只能看到本模式的工具，Plan 专属工具不进 GOAL 子代理 schema。
    /// </summary>
    private List<AITool> ResolveGoalTools()
    {
        var capability = toolCapabilityResolver.Resolve(WorkingMode.Goal);
        return toolCatalog.Tools
            .Where(tool => capability.AllowedToolNames.Contains(tool.Name))
            .ToList<AITool>();
    }

    /// <summary>
    /// GOAL mode streaming: runs through the durable MAF pipeline
    /// (<see cref="GoalMode.GoalWorkflowHost"/> + GoalRun aggregate), bridges
    /// OrchestrationEvent → QueryEvent via the shared skeleton, then emits
    /// Goal-produced QueryEvents (text deltas, summary, etc.).
    /// </summary>
    public async IAsyncEnumerable<QueryEvent> StreamGoalAsync(
        InteractiveSession session,
        string text,
        string currentModelId,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var conversationId = session.SessionManager.ForegroundConversation?.Id
            ?? throw new InvalidOperationException("Goal mode requires an active conversation.");
        var budget = ModeBudgetSettings.FromSettings(configManager.Current.Effective);
        var tools = ResolveGoalTools();
        // Goal definition hash is computed from the body only: the harness fragment is deliberately
        // excluded so existing checkpoints stay valid, and the same source (session.SystemPrompt)
        // feeds both BeginAsync here and the resume path below — the two must never diverge.
        var systemPromptHash = GoalWorkflowCompiler.ComputeTextHash(session.SystemPrompt);
        var toolCapabilityHash = GoalWorkflowCompiler.ComputeToolCapabilityHash(tools.Select(tool => tool.Name));
        // Checkpoint 序列化选项单一来源：BeginAsync 计算 DefinitionHash 与执行期 Compile
        // 校验必须使用同一实例，否则 hash 恒不一致（GoalRun definition hash mismatch）。
        var serializerOptions = new JsonSerializerOptions();
        var originalWorkingDirectory = wd.WorkingDirectory;
        var goalRun = await goalRunApplicationService.BeginAsync(
            conversationId,
            text,
            originalWorkingDirectory,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions,
            ct).ConfigureAwait(false);

        var effectiveWorktreeDir = GoalWorkspacePathResolver.ResolveEffectiveWorkingDirectory(goalRun);

        // GOAL 落转录①：目标诉求与 mode 元数据随会话持久化，供列表、恢复和后续上下文使用。
        session.SessionManager.SetForegroundMode("goal");
        await session.SessionManager.AppendUserMessageAsync(goalRun.SessionId, text, ct).ConfigureAwait(false);

        // 展示 Goal 隔离 worktree 位置，便于用户定位新建目录。
        if (goalRun.Workspace is { } workspace)
        {
            yield return new NoticeEvent(
                $"🔒 Goal 隔离 worktree：{effectiveWorktreeDir}"
                + $"（分支 {workspace.WorktreeBranch}，基于 {workspace.TargetBranch}）");
        }

        await foreach (var evt in ExecuteGoalWorkflowStreamAsync(
            goalRun,
            session.SystemPrompt,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions,
            tools,
            budget,
            originalWorkingDirectory,
            effectiveWorktreeDir,
            session,
            imagePaths,
            ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 从 Checkpoint 恢复 GOAL 模式执行（流式）。
    /// 通过 <see cref="GoalMode.GoalWorkflowHost"/> 开启新执行世代，
    /// 业务事实来自 GoalRun 聚合与步骤 Git 回执。
    /// </summary>
    public async IAsyncEnumerable<QueryEvent> StreamResumeGoalAsync(
        SessionId sessionId,
        string currentModelId,
        string systemPrompt,
        [EnumeratorCancellation] CancellationToken ct,
        InteractiveSession? session = null)
    {
        var goalRun = await goalRunApplicationService.GetBySessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No GoalRun exists for session '{sessionId}'.");
        if (goalRun.IsTerminal)
            throw new InvalidOperationException($"GoalRun '{goalRun.Id}' is already terminal.");
        var budget = ModeBudgetSettings.FromSettings(configManager.Current.Effective);
        var tools = ResolveGoalTools();
        var systemPromptHash = GoalWorkflowCompiler.ComputeTextHash(systemPrompt);
        var toolCapabilityHash = GoalWorkflowCompiler.ComputeToolCapabilityHash(tools.Select(tool => tool.Name));
        // 与 StreamGoalAsync 同源：hash 契约含「显式 JsonSerializerOptions」签名成分，
        // 漏传会使 DescribeSerializerOptions 落到 "default"，与 Begin 持久化的 hash 恒不一致。
        var serializerOptions = new JsonSerializerOptions();
        var expectedHash = GoalWorkflowCompiler.ComputeDefinitionHash(
            goalRun,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions);
        if (!string.Equals(expectedHash, goalRun.DefinitionHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Goal workflow definition changed and cannot be resumed.");

        var originalWorkingDirectory = session?.SessionManager.WorkingDirectory ?? goalRun.WorkingDirectory;
        var effectiveWorktreeDir = GoalWorkspacePathResolver.ResolveEffectiveWorkingDirectory(goalRun);

        await foreach (var evt in ExecuteGoalWorkflowStreamAsync(
            goalRun,
            systemPrompt,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions,
            tools,
            budget,
            originalWorkingDirectory,
            effectiveWorktreeDir,
            session,
            imagePaths: null,
            ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 共享 Goal 工作流流式执行骨架（首次启动与 Checkpoint 恢复共用）：
    /// 负责 worktree 切换与恢复、TUI 事件通道合并、GoalWorkflowHost 执行世代驱动与终态事件写回。
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> ExecuteGoalWorkflowStreamAsync(
        GoalRun goalRun,
        string systemPrompt,
        string currentModelId,
        string systemPromptHash,
        string toolCapabilityHash,
        JsonSerializerOptions serializerOptions,
        List<AITool> tools,
        ModeBudgetSettings budget,
        string originalWorkingDirectory,
        string effectiveWorktreeDir,
        InteractiveSession? session,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var switchWorkingDir = session is not null
            && !string.Equals(effectiveWorktreeDir, originalWorkingDirectory, StringComparison.OrdinalIgnoreCase);

        // 用量基线（会话桶四字段快照）：终态 DoneEvent 的用量口径 = 本次执行世代内
        // 子目标 + Judge + 分解调用的真实四项增量（含缓存），对齐 BUILD/PLAN 的全口径；
        // 账本缺失时回退 GoalBudget 的 input+output 原始口径。
        var usageBaseline = tokenLedger.GetSessionUsage(goalRun.SessionId)?.Snapshot();

        // 执行任务声明于 try 外：取消路径从读取循环直达 finally，
        // 「await 执行任务后才恢复工作目录」必须覆盖取消场景。
        Task runTask = Task.CompletedTask;

        if (switchWorkingDir)
        {
            await session!.SessionManager.ChangeWorkingDirectoryAsync(effectiveWorktreeDir, ct).ConfigureAwait(false);
            yield return new WorkingDirectoryChangedEvent(effectiveWorktreeDir);
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = runCts.Token;
        try
        {
            var mergedChannel = Channel.CreateUnbounded<QueryEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
            });
            var options = new GoalRunOptions
            {
                Goal = goalRun.Goal,
                WorkingDirectory = effectiveWorktreeDir,
                SystemPrompt = systemPrompt,
                ModelId = currentModelId,
                Tools = tools,
                MaxTurnsPerSubGoal = budget.MaxTurnsPerSubGoal,
                Budget = budget.ToGoalBudget(),
                // 子目标记账归属目标会话；全局预算上限与 BUILD/PLAN 同源归一化（0/负值=不设限）。
                SessionId = goalRun.SessionId,
                MaxBudgetTokens = configManager.Current.Effective.MaxBudgetTokens > 0
                    ? configManager.Current.Effective.MaxBudgetTokens
                    : null,
                OrchestrationEventSink = orchEvt => ForwardOrchestrationEvent(orchEvt, mergedChannel.Writer),
                ImagePaths = imagePaths,
            };
            var runtime = goalRuntimeFactory.Create(new GoalWorkflowRuntimeContext(
                options,
                mergedChannel.Writer,
                static () => new EditTransaction(),
                PromptOnBudgetExhausted: true,
                TokenLedger: tokenLedger));
            runTask = Task.Run(async () =>
            {
                try
                {
                    using var cwdScope = OneCodeAgentRunContext.UseWorkingDirectory(effectiveWorktreeDir);
                    _ = await goalWorkflowHost.RunNextAsync(
                        goalRun,
                        currentModelId,
                        systemPromptHash,
                        toolCapabilityHash,
                        runtime,
                        serializerOptions,
                        ct: runCt).ConfigureAwait(false);
                    var final = await goalRunApplicationService.GetAsync(goalRun.Id, runCt).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' disappeared after execution.");
                    // 先落转录再发终态：转录失败只能产生 Error，不能形成 Done → Error。
                    if (session?.SessionManager is { } transcriptManager)
                    {
                        await transcriptManager.AppendAssistantMessageAsync(
                            goalRun.SessionId,
                            BuildGoalTranscriptSummary(final),
                            new TokenUsage(
                                checked((int)Math.Min(int.MaxValue, final.Budget.TotalInputTokens)),
                                checked((int)Math.Min(int.MaxValue, final.Budget.TotalOutputTokens))),
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    mergedChannel.Writer.TryWrite(ToGoalResult(final));
                    mergedChannel.Writer.TryWrite(new DoneEvent(
                        FullText: null,
                        Usage: BuildDoneUsage(
                            final,
                            usageBaseline,
                            tokenLedger.GetSessionUsage(final.SessionId)?.Snapshot()),
                        TurnsCompleted: final.Executions.Sum(execution => execution.Attempts),
                        TerminalReason: final.TerminalReason ?? RunTerminalReasonMap.FromGoalState(final.State),
                        SessionId: final.SessionId,
                        TransactionRolledBack: false,
                        ValidationFailureSummary: final.FailureSummary));
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        var cancelledRun = await goalRunApplicationService.GetAsync(
                            goalRun.Id, CancellationToken.None).ConfigureAwait(false);
                        if (session?.SessionManager is { } cancelTranscriptManager)
                        {
                            await cancelTranscriptManager.AppendAssistantMessageAsync(
                                goalRun.SessionId,
                                BuildGoalTranscriptSummary(cancelledRun, cancelled: true),
                                new TokenUsage(0, 0),
                                CancellationToken.None).ConfigureAwait(false);
                        }

                        mergedChannel.Writer.TryWrite(new DoneEvent(
                            FullText: null,
                            Usage: cancelledRun is { } run
                                ? BuildDoneUsage(run, usageBaseline, tokenLedger.GetSessionUsage(run.SessionId)?.Snapshot())
                                : new TokenUsage(0, 0),
                            TurnsCompleted: cancelledRun?.Executions.Sum(execution => execution.Attempts) ?? 0,
                            TerminalReason: RunTerminalReason.Cancelled,
                            SessionId: goalRun.SessionId,
                            TransactionRolledBack: false,
                            ValidationFailureSummary: cancelledRun?.FailureSummary));
                    }
                    catch (Exception ex)
                    {
                        mergedChannel.Writer.TryWrite(new ErrorEvent(ex.Message));
                    }
                }
                catch (SessionDeletedException)
                {
                    mergedChannel.Writer.TryWrite(new DoneEvent(
                        FullText: null,
                        Usage: new TokenUsage(0, 0),
                        TurnsCompleted: goalRun.Executions.Sum(execution => execution.Attempts),
                        TerminalReason: RunTerminalReason.Cancelled,
                        SessionId: goalRun.SessionId,
                        TransactionRolledBack: false,
                        ValidationFailureSummary: null));
                }
                catch (Exception ex)
                {
                    mergedChannel.Writer.TryWrite(new ErrorEvent(ex.Message));
                }
                finally
                {
                    mergedChannel.Writer.TryComplete();
                }
            }, runCt);

            await foreach (var evt in mergedChannel.Reader.ReadAllAsync(runCt).ConfigureAwait(false))
            {
                yield return evt;
                runCt.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            // 消费方提前退出也先取消执行任务，再有界等待收尾，最后恢复会话目录。
            runCts.Cancel();
            if (!runTask.IsCompleted)
            {
                _ = await Task.WhenAny(
                    runTask,
                    Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)).ConfigureAwait(false);
            }

            if (switchWorkingDir)
            {
                try
                {
                    await session!.SessionManager.ChangeWorkingDirectoryAsync(originalWorkingDirectory, CancellationToken.None).ConfigureAwait(false);
                }
                catch (SessionDeletedException)
                {
                    // 会话已删除，取消终态已发出；不再为目录恢复制造第二个异常终态。
                }
            }
        }

        if (switchWorkingDir)
        {
            yield return new WorkingDirectoryChangedEvent(originalWorkingDirectory);
        }
    }

    /// <summary>把编排事件投影到 UI 流；所有正文增量只经此入口转发，Harness 内部工具不进入对话流。</summary>
    internal static void ForwardOrchestrationEvent(OrchestrationEvent orchEvt, ChannelWriter<QueryEvent> writer)
    {
        if (MapOrchestrationEvent(orchEvt) is { } mapped)
            writer.TryWrite(mapped);
    }

    private static QueryEvent? MapOrchestrationEvent(OrchestrationEvent orchEvt) => orchEvt switch
    {
        OrchestrationEvent.AgentMessage am
            => new AgentMessageEvent(am.AgentName, am.Content),
        OrchestrationEvent.ToolStart ts when !HarnessProviderTools.Names.Contains(ts.Name)
            => new ToolStartEvent(ts.ToolId, ts.Name, ts.ToolInput, ts.AgentName),
        OrchestrationEvent.ToolDone td when !HarnessProviderTools.Names.Contains(td.Name)
            => new ToolDoneEvent(td.ToolId, td.Name, td.IsError, td.Result, td.ToolInput, td.AgentName),
        OrchestrationEvent.TextDelta td
            => new TextDeltaEvent(td.Text),
        OrchestrationEvent.FileChanged fc
            => new FileChangeEvent(fc.FileName, fc.AddedLines, fc.RemovedLines, fc.AgentName),
        OrchestrationEvent.Error err
            => new ErrorEvent(err.Message),
        _ => null,
    };

    /// <summary>
    /// 终态用量口径：优先取会话桶四项增量（本次执行世代内子目标 + Judge + 分解的真实消耗，
    /// 含缓存读/写，对齐 BUILD/PLAN）；账本或基线缺失时回退 GoalBudget 的 input+output。
    /// </summary>
    internal static TokenUsage BuildDoneUsage(
        GoalRun run,
        SessionTokenUsage? baseline,
        SessionTokenUsage? current)
    {
        if (current is null || baseline is null)
            return new TokenUsage(
                checked((int)Math.Min(int.MaxValue, run.Budget.TotalInputTokens)),
                checked((int)Math.Min(int.MaxValue, run.Budget.TotalOutputTokens)));

        return new TokenUsage(
            checked((int)Math.Min(int.MaxValue, Math.Max(0, current.TotalInputTokens - baseline.TotalInputTokens))),
            checked((int)Math.Min(int.MaxValue, Math.Max(0, current.TotalOutputTokens - baseline.TotalOutputTokens))),
            checked((int)Math.Min(int.MaxValue, Math.Max(0, current.TotalCacheReadTokens - baseline.TotalCacheReadTokens))),
            checked((int)Math.Min(int.MaxValue, Math.Max(0, current.TotalCacheWriteTokens - baseline.TotalCacheWriteTokens))));
    }

    /// <summary>GOAL 终态摘要（落转录的 assistant 消息正文）。</summary>
    private static string BuildGoalTranscriptSummary(GoalRun? run, bool cancelled = false)
    {
        if (run is null)
            return "[GOAL] Execution was cancelled before any state was recorded.";

        var completed = run.Plan.Count(step => step.State == GoalStepState.Completed);
        var failed = run.Plan.Count(step => step.State == GoalStepState.Failed);
        var skipped = run.Plan.Count(step => step.State == GoalStepState.Skipped);
        var header = cancelled
            ? $"[GOAL] Cancelled — {completed}/{run.Plan.Count} sub-goals completed before cancellation."
            : $"[GOAL] {run.State} — {completed}/{run.Plan.Count} completed, {failed} failed, {skipped} skipped.";
        var steps = string.Join("\n", run.Plan
            .Where(step => step.State is GoalStepState.Completed or GoalStepState.Failed)
            .Select(step => $"  {(step.State == GoalStepState.Completed ? "✓" : "✗")} #{step.Id}: {step.Description}"));
        var conclusion = run.Executions
            .LastOrDefault(execution => execution.State == GoalStepState.Completed && !string.IsNullOrWhiteSpace(execution.AgentOutput))
            ?.AgentOutput;
        return string.IsNullOrWhiteSpace(conclusion)
            ? $"{header}\n{steps}"
            : $"{header}\n{steps}\nFinal output:\n{conclusion}";
    }

    /// <summary>构建 GOAL 结果投影（完成/失败/跳过计数、验证门摘要与产物文件列表）。</summary>
    private static GoalResultEvent ToGoalResult(GoalRun run)
    {
        var changedFiles = run.PublishReceipt?.ChangedFiles
            ?? run.Executions.SelectMany(e => e.ChangedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lastCompletedOutput = run.Executions
            .LastOrDefault(e => e.State == GoalStepState.Completed && !string.IsNullOrWhiteSpace(e.AgentOutput))
            ?.AgentOutput;
        return new(
            run.Plan.Count(step => step.State == GoalStepState.Completed),
            run.Plan.Count(step => step.State == GoalStepState.Failed),
            run.Plan.Count(step => step.State == GoalStepState.Skipped),
            run.Plan.Count,
            run.State == GoalRunState.Completed && run.PublishReceipt is not null,
            run.Plan.Where(step => step.State == GoalStepState.Completed).Select(step => step.Description).ToArray(),
            run.Plan.Where(step => step.State == GoalStepState.Failed).Select(step => step.Description).ToArray(),
            run.Plan.Where(step => step.State == GoalStepState.Skipped).Select(step => step.Description).ToArray(),
            run.FinalValidation.Count == 0
                ? run.FailureSummary ?? "Goal execution did not produce final validation evidence."
                : string.Join("\n", run.FinalValidation.Select(gate =>
                    $"[{(gate.Skipped ? "SKIP" : gate.Passed ? "PASS" : "FAIL")}] {gate.Gate}: {gate.Summary}")),
            ChangedFiles: changedFiles,
            FinalConclusion: lastCompletedOutput);
    }
}
