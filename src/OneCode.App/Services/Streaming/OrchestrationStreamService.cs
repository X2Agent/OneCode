using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// Bridges GOAL orchestration backend to the TUI streaming pipeline.
/// GOAL mode runs through the durable MAF pipeline
/// (<see cref="GoalMode.GoalWorkflowHost"/>), converting
/// <see cref="OrchestrationEvent"/> streams into <see cref="TuiEvent"/> streams.
/// </summary>
public sealed class OrchestrationStreamService(
    IConfigManager configManager,
    IToolCatalog toolCatalog,
    IWorkingDirectoryAccessor wd,
    IGoalRunApplicationService goalRunApplicationService,
    GoalWorkflowHost goalWorkflowHost,
    IGoalWorkflowRuntimeFactory goalRuntimeFactory)
{
    /// <summary>
    /// GOAL mode streaming: runs through the durable MAF pipeline
    /// (<see cref="GoalMode.GoalWorkflowHost"/> + GoalRun aggregate), bridges
    /// OrchestrationEvent → TuiEvent via the shared skeleton, then emits
    /// Goal-produced TuiEvents (text deltas, summary, etc.).
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamGoalAsync(
        InteractiveSession session,
        string text,
        string currentModelId,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var conversationId = session.SessionManager.ForegroundConversation?.Id
            ?? throw new InvalidOperationException("Goal mode requires an active conversation.");
        var budget = ModeBudgetSettings.FromSettings(configManager.Current.Effective);
        var tools = toolCatalog.Tools.ToList<AITool>();
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

        // 展示 Goal 隔离 worktree 位置，便于用户定位新建目录。
        if (goalRun.Workspace is { } workspace)
        {
            yield return new TuiNotice(
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
    public async IAsyncEnumerable<TuiEvent> StreamResumeGoalAsync(
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
        var tools = toolCatalog.Tools.ToList<AITool>();
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
    private async IAsyncEnumerable<TuiEvent> ExecuteGoalWorkflowStreamAsync(
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

        if (switchWorkingDir)
        {
            await session!.SessionManager.ChangeWorkingDirectoryAsync(effectiveWorktreeDir, ct).ConfigureAwait(false);
            yield return new TuiWorkingDirectoryChanged(effectiveWorktreeDir);
        }

        try
        {
            var mergedChannel = Channel.CreateUnbounded<TuiEvent>(new UnboundedChannelOptions
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
                OrchestrationEventSink = orchEvt =>
                {
                    if (TuiEventMapper.MapOrchestrationEventToTuiEvent(orchEvt) is { } mapped
                        && mapped is not TuiTextDelta)
                    {
                        mergedChannel.Writer.TryWrite(mapped);
                    }
                },
                ImagePaths = imagePaths,
            };
            var runtime = goalRuntimeFactory.Create(new GoalWorkflowRuntimeContext(
                options,
                mergedChannel.Writer,
                static () => new EditTransaction(),
                PromptOnBudgetExhausted: true));
            var runTask = Task.Run(async () =>
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
                        ct: ct).ConfigureAwait(false);
                    var final = await goalRunApplicationService.GetAsync(goalRun.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' disappeared after execution.");
                    mergedChannel.Writer.TryWrite(ToGoalResult(final));
                    mergedChannel.Writer.TryWrite(new TuiDone(
                        InputTokens: checked((int)Math.Min(int.MaxValue, final.Budget.TotalInputTokens)),
                        OutputTokens: checked((int)Math.Min(int.MaxValue, final.Budget.TotalOutputTokens)),
                        TerminalReason: final.TerminalReason ?? RunTerminalReasonMap.FromGoalState(final.State),
                        TurnsCompleted: final.Executions.Sum(execution => execution.Attempts),
                        SessionId: final.SessionId,
                        TransactionRolledBack: false,
                        ValidationFailureSummary: final.FailureSummary));
                }
                catch (OperationCanceledException)
                {
                    // 取消即正常收尾，不再向下游写入错误。
                }
                catch (Exception ex)
                {
                    mergedChannel.Writer.TryWrite(new TuiError(ex.Message));
                }
                finally
                {
                    mergedChannel.Writer.TryComplete();
                }
            }, ct);

            await foreach (var evt in mergedChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
            await runTask.ConfigureAwait(false);
        }
        finally
        {
            if (switchWorkingDir)
            {
                await session!.SessionManager.ChangeWorkingDirectoryAsync(originalWorkingDirectory, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (switchWorkingDir)
        {
            yield return new TuiWorkingDirectoryChanged(originalWorkingDirectory);
        }
    }

    /// <summary>构建 GOAL 结果投影（完成/失败/跳过计数、验证门摘要与产物文件列表）。</summary>
    private static TuiGoalResult ToGoalResult(GoalRun run)
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
