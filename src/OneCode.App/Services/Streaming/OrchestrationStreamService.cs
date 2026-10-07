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
        var goalRun = await goalRunApplicationService.BeginAsync(
            conversationId,
            text,
            wd.WorkingDirectory,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions,
            ct).ConfigureAwait(false);
        // 脏工作树提示：WIP 已带入隔离 worktree，Goal 直接基于未提交改动执行。
        if (goalRun.Workspace is { CarriedUncommittedCount: > 0 } carried)
        {
            var samples = carried.CarriedUncommittedPaths ?? [];
            var sampleText = samples.Count == 0
                ? string.Empty
                : "\n" + string.Join("\n", samples.Take(8).Select(p => $"  · {p}"))
                    + (samples.Count > 8 ? "\n  · …" : string.Empty);
            yield return new TuiNotice(
                $"⚠ 目标工作区有 {carried.CarriedUncommittedCount} 个未提交改动，已带入 Goal 隔离 worktree，"
                + "发布时会随结果一起提交。" + sampleText);
        }

        // 展示 Goal 隔离 worktree 位置，便于用户定位新建目录。
        if (goalRun.Workspace is { } workspace)
        {
            yield return new TuiNotice(
                $"🔒 Goal 隔离 worktree：{workspace.IsolatedPath}"
                + $"（分支 {workspace.WorktreeBranch}，基于 {workspace.TargetBranch}）");
        }

        var mergedChannel = Channel.CreateUnbounded<TuiEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        var options = new GoalRunOptions
        {
            Goal = goalRun.Goal,
            WorkingDirectory = goalRun.Workspace?.IsolatedPath ?? goalRun.WorkingDirectory,
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
            static () => new EditTransaction()));
        var runTask = Task.Run(async () =>
        {
            try
            {
                var result = await goalWorkflowHost.RunNextAsync(
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
                _ = result;
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

    /// <summary>
    /// 从 Checkpoint 恢复 GOAL 模式执行（流式）。
    /// 通过 <see cref="GoalMode.GoalWorkflowHost"/> 开启新执行世代，
    /// 业务事实来自 GoalRun 聚合与步骤 Git 回执。
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamResumeGoalAsync(
        SessionId sessionId,
        string currentModelId,
        string systemPrompt,
        [EnumeratorCancellation] CancellationToken ct)
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
        var mergedChannel = Channel.CreateUnbounded<TuiEvent>(new UnboundedChannelOptions { SingleReader = true });
        var runtime = goalRuntimeFactory.Create(new GoalWorkflowRuntimeContext(
            new GoalRunOptions
            {
                Goal = goalRun.Goal,
                WorkingDirectory = goalRun.Workspace?.IsolatedPath ?? goalRun.WorkingDirectory,
                ModelId = currentModelId,
                Tools = tools,
                MaxTurnsPerSubGoal = budget.MaxTurnsPerSubGoal,
                Budget = budget.ToGoalBudget(),
            },
            mergedChannel.Writer,
            static () => new EditTransaction()));
        var runTask = Task.Run(async () =>
        {
            try
            {
                _ = await goalWorkflowHost.RunNextAsync(
                    goalRun,
                    currentModelId,
                    systemPromptHash,
                    toolCapabilityHash,
                    runtime,
                    serializerOptions,
                    ct: ct).ConfigureAwait(false);
                var final = await goalRunApplicationService.GetAsync(goalRun.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' disappeared after resume.");
                mergedChannel.Writer.TryWrite(ToGoalResult(final));
                mergedChannel.Writer.TryWrite(new TuiDone(
                    checked((int)Math.Min(int.MaxValue, final.Budget.TotalInputTokens)),
                    checked((int)Math.Min(int.MaxValue, final.Budget.TotalOutputTokens)),
                    final.TerminalReason ?? RunTerminalReasonMap.FromGoalState(final.State),
                    final.Executions.Sum(execution => execution.Attempts),
                    SessionId: final.SessionId,
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

    /// <summary>构建 GOAL 结果投影（完成/失败/跳过计数与验证门摘要）。</summary>
    private static TuiGoalResult ToGoalResult(GoalRun run)
        => new(
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
                    $"[{(gate.Skipped ? "SKIP" : gate.Passed ? "PASS" : "FAIL")}] {gate.Gate}: {gate.Summary}")));
}
