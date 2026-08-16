using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Config;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// Bridges GOAL/TEAM orchestration backends to the TUI streaming pipeline.
/// GOAL mode runs through the durable MAF pipeline
/// (<see cref="GoalMode.GoalWorkflowHost"/>), TEAM mode through
/// <see cref="ITeamOrchestrationService"/>; both convert
/// <see cref="OrchestrationEvent"/> streams into <see cref="TuiEvent"/> streams.
/// </summary>
/// <remarks>
/// Extracted from <see cref="InteractiveModeExecutor"/>. The shared skeleton
/// <see cref="StreamOrchestrationAsync"/> eliminates the duplicated
/// channel-create / background-task / drain pattern between Goal and Team.
/// Channels are unbounded because <c>OrchestrationEventSink</c> is a sync
/// <c>Action</c> callback that cannot await <c>WriteAsync</c>; bounded channels
/// with <c>TryWrite</c> would silently drop events. Memory growth is bounded
/// in practice: events are tiny (~100 bytes) and the channel is scoped to a
/// single goal/team operation with continuous drain.
/// </remarks>
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
        var maxSubGoalAttempts = configManager.Current.Effective.Get("goal.maxSubGoalAttempts", 20);
        var maxTurnsPerSubGoal = configManager.Current.Effective.Get("goal.maxTurnsPerSubGoal", 50);
        var tools = toolCatalog.Tools.ToList<AITool>();
        var systemPromptHash = GoalWorkflowCompiler.ComputeTextHash(session.SystemPrompt);
        var toolCapabilityHash = GoalWorkflowCompiler.ComputeToolCapabilityHash(tools.Select(tool => tool.Name));
        var goalRun = await goalRunApplicationService.BeginAsync(
            conversationId,
            text,
            wd.WorkingDirectory,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash,
            ct).ConfigureAwait(false);
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
            MaxTurnsPerSubGoal = maxTurnsPerSubGoal,
            Budget = BuildGoalBudgetFromSettings(configManager, maxSubGoalAttempts),
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
                    new JsonSerializerOptions(),
                    ct: ct).ConfigureAwait(false);
                var final = await goalRunApplicationService.GetAsync(goalRun.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' disappeared after execution.");
                mergedChannel.Writer.TryWrite(ToGoalResult(final));
                mergedChannel.Writer.TryWrite(new TuiDone(
                    InputTokens: checked((int)Math.Min(int.MaxValue, final.Budget.TotalInputTokens)),
                    OutputTokens: checked((int)Math.Min(int.MaxValue, final.Budget.TotalOutputTokens)),
                    TerminalReason: final.TerminalReason ?? ResolveTerminalReason(final.State),
                    TurnsCompleted: final.Executions.Sum(execution => execution.Attempts),
                    SessionId: final.SessionId,
                    TransactionRolledBack: false,
                    ValidationFailureSummary: final.FailureSummary));
                _ = result;
            }
            catch (OperationCanceledException)
            {
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
    /// TEAM mode streaming: delegates to ITeamOrchestrationService, bridges
    /// OrchestrationEvent → TuiEvent via the shared skeleton, then emits a
    /// final TuiDone with token statistics.
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamTeamAsync(
        InteractiveSession session,
        ITeamOrchestrationService teamService,
        string text,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var teamName = teamService.ResolveActiveTeam() ?? teamService.RegisteredTeams[0];

        // Config 为默认值：遵循团队 YAML。只有用户显式切换策略时才覆盖配置。
        var overrideMode = ResolveTeamOverride(session.ModeController.Strategy);

        // 编排模式和成员清单属于详情信息；主对话只投影当前可理解阶段。
        yield return new TuiModeProgress(
            WorkingMode.Team,
            $"团队 {teamName} 正在分析任务…");

        // TeamRunResult is captured inside the runner and read after draining.
        TeamRunResult? teamResult = null;
        var receivedAnyEvent = false;

        await foreach (var evt in StreamOrchestrationAsync(async (sink, token) =>
        {
            teamResult = await teamService.RunTeamStreamingAsync(
                teamName, text, sink, token,
                overrideMode: overrideMode,
                imagePaths: imagePaths,
                sessionId: session.SessionManager.ForegroundConversation?.Id).ConfigureAwait(false);
        }, ct).ConfigureAwait(false))
        {
            receivedAnyEvent = true;
            yield return evt;
        }

        // 用户取消：跳过 missing-output 友好报错与终态投影（消费端以 OCE 结束，
        // TUI 显示 "(cancelled)"），避免把取消误报为配置错误。
        if (ct.IsCancellationRequested)
            yield break;

        // (no output) 检测：如果整个流程没有产生任何中间事件，且最终输出为 "(no output)" 或空，
        // 说明 MAF 工作流未产生任何 Agent 响应——通常是 ChatClient 配置问题（如 API key 无效）
        // 或团队成员 Prompt 加载失败。给出友好提示而非静默完成。
        if (!receivedAnyEvent || IsMissingTeamOutput(teamResult))
        {
            yield return new TuiError(
                "团队执行未产生任何输出。可能原因：\n" +
                "  • ChatClient/API 配置异常（检查 API key 和模型设置）\n" +
                "  • 团队成员 Prompt 加载失败（查看日志中 'Failed to load' 相关警告）\n" +
                "  • MAF 工作流启动失败（查看日志中 'workflow' 相关错误）\n" +
                "建议：先在 BUILD 模式验证 ChatClient 配置正常，再使用 TEAM 模式。");
        }

        if (teamResult?.Delivery is { } delivery)
        {
            yield return new TuiTeamDelivery(delivery);
        }
        else if (teamResult is { } completedResult)
        {
            var progressState = completedResult.Status == TeamRunStatus.Succeeded && !completedResult.HadFailures
                ? ModeProgressState.Completed
                : ModeProgressState.Failed;
            yield return new TuiModeProgress(
                WorkingMode.Team,
                progressState == ModeProgressState.Completed ? "团队任务已完成" : "团队任务未完成",
                progressState);
        }

        var terminalReason = teamResult switch
        {
            { MaxTurnsReached: true } => OneCode.Core.Build.BuildTerminalReason.TurnLimitReached,
            { Status: TeamRunStatus.Succeeded } => OneCode.Core.Build.BuildTerminalReason.Completed,
            { Status: TeamRunStatus.Cancelled } => OneCode.Core.Build.BuildTerminalReason.Cancelled,
            { Status: TeamRunStatus.Blocked } => OneCode.Core.Build.BuildTerminalReason.Blocked,
            { Status: TeamRunStatus.RolledBack or TeamRunStatus.Failed } => OneCode.Core.Build.BuildTerminalReason.ValidationFailed,
            { Error: not null } => OneCode.Core.Build.BuildTerminalReason.AgentException,
            _ => OneCode.Core.Build.BuildTerminalReason.Completed,
        };
        var rolledBack = teamResult?.Status is TeamRunStatus.RolledBack or TeamRunStatus.Failed;
        yield return new TuiDone(
            InputTokens: (int)(teamResult?.InputTokens ?? 0),
            OutputTokens: (int)(teamResult?.OutputTokens ?? 0),
            TerminalReason: terminalReason,
            TurnsCompleted: teamResult?.TurnsCompleted ?? 0,
            SessionId: teamResult?.SessionId,
            TransactionRolledBack: rolledBack,
            ValidationFailureSummary: rolledBack ? BuildTeamFailureSummary(teamResult) : null);
    }

    internal static bool IsMissingTeamOutput(TeamRunResult? result)
        => result is null
            || result.TurnsCompleted == 0
            || string.IsNullOrWhiteSpace(result.Output)
            || string.Equals(result.Output.Trim(), "(no output)", StringComparison.Ordinal);

    internal static TeamOrchestrationMode? ResolveTeamOverride(TeamStrategy strategy) => strategy switch
    {
        TeamStrategy.Magentic => TeamOrchestrationMode.Magentic,
        TeamStrategy.GroupChat => TeamOrchestrationMode.GroupChat,
        _ => null,
    };

    private static string? BuildTeamFailureSummary(TeamRunResult? result)
    {
        if (result is null)
            return null;

        var lines = new List<string>();
        if (result.Delivery is { } delivery)
        {
            foreach (var gate in delivery.Gates.Where(g => g.Required && g.Status != QualityGateStatus.Passed))
            {
                lines.Add($"质量门 {gate.GateId} [{gate.Kind}]：{gate.Status} — {gate.Summary}");
                foreach (var evidence in gate.Evidence.Take(3))
                    lines.Add($"  证据：{evidence}");
            }
        }

        if (lines.Count == 0 && result.Error is { } error)
            lines.Add(error.Detail);
        if (lines.Count == 0 && result.TurnsCompleted == 0)
            lines.Add("团队工作流没有收到任何 Agent 响应（turns=0）。");
        return lines.Count == 0 ? result.Delivery?.Summary : string.Join("\n", lines);
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
        var maxSubGoalAttempts = configManager.Current.Effective.Get("goal.maxSubGoalAttempts", 20);
        var maxTurnsPerSubGoal = configManager.Current.Effective.Get("goal.maxTurnsPerSubGoal", 50);
        var tools = toolCatalog.Tools.ToList<AITool>();
        var systemPromptHash = GoalWorkflowCompiler.ComputeTextHash(systemPrompt);
        var toolCapabilityHash = GoalWorkflowCompiler.ComputeToolCapabilityHash(tools.Select(tool => tool.Name));
        var expectedHash = GoalWorkflowCompiler.ComputeDefinitionHash(
            goalRun,
            currentModelId,
            systemPromptHash,
            toolCapabilityHash);
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
                MaxTurnsPerSubGoal = maxTurnsPerSubGoal,
                Budget = BuildGoalBudgetFromSettings(configManager, maxSubGoalAttempts),
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
                    new JsonSerializerOptions(),
                    ct: ct).ConfigureAwait(false);
                var final = await goalRunApplicationService.GetAsync(goalRun.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' disappeared after resume.");
                mergedChannel.Writer.TryWrite(ToGoalResult(final));
                mergedChannel.Writer.TryWrite(new TuiDone(
                    checked((int)Math.Min(int.MaxValue, final.Budget.TotalInputTokens)),
                    checked((int)Math.Min(int.MaxValue, final.Budget.TotalOutputTokens)),
                    final.TerminalReason ?? ResolveTerminalReason(final.State),
                    final.Executions.Sum(execution => execution.Attempts),
                    SessionId: final.SessionId,
                    ValidationFailureSummary: final.FailureSummary));
            }
            catch (OperationCanceledException)
            {
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
    /// 从 Checkpoint 恢复 TEAM 模式执行（流式）。
    /// 委托给 <see cref="ITeamOrchestrationService.ResumeTeamStreamingAsync"/>，桥接事件流。
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamResumeTeamAsync(
        ITeamOrchestrationService teamService,
        SessionId sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TuiModeProgress(
            WorkingMode.Team,
            "正在恢复团队任务…");

        TeamRunResult? teamResult = null;

        await foreach (var evt in StreamOrchestrationAsync(async (sink, token) =>
        {
            teamResult = await teamService.ResumeTeamStreamingAsync(
                sessionId, sink, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false))
        {
            yield return evt;
        }

        // 用户取消：跳过恢复结果投影（消费端以 OCE 结束，TUI 显示 "(cancelled)"）。
        if (ct.IsCancellationRequested)
            yield break;

        var progressState = teamResult?.HadFailures == true
            ? ModeProgressState.Failed
            : ModeProgressState.Completed;
        yield return new TuiModeProgress(
            WorkingMode.Team,
            progressState == ModeProgressState.Completed ? "团队任务已恢复并完成" : "团队任务恢复后未完成",
            progressState);
        yield return new TuiDone(
            InputTokens: (int)(teamResult?.InputTokens ?? 0),
            OutputTokens: (int)(teamResult?.OutputTokens ?? 0),
            TerminalReason: (teamResult?.MaxTurnsReached ?? false) ? OneCode.Core.Build.BuildTerminalReason.TurnLimitReached : OneCode.Core.Build.BuildTerminalReason.Completed,
            TurnsCompleted: teamResult?.TurnsCompleted ?? 0,
            SessionId: teamResult?.SessionId ?? sessionId);
    }

    /// <summary>
    /// Shared skeleton for GOAL/TEAM streaming: creates a channel, runs
    /// <paramref name="orchestrationRunner"/> on a background task (forwarding
    /// OrchestrationEvents to the channel), and drains the channel while mapping
    /// OrchestrationEvent → TuiEvent via <see cref="TuiEventMapper"/>.
    /// </summary>
    private static async IAsyncEnumerable<TuiEvent> StreamOrchestrationAsync(
        Func<Action<OrchestrationEvent>, CancellationToken, Task> orchestrationRunner,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<OrchestrationEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        var runTask = Task.Run(async () =>
        {
            try
            {
                await orchestrationRunner(evt => channel.Writer.TryWrite(evt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户取消不是错误：不产生 Error 事件，通道正常完成；消费端通过已
                // 取消的 token 以 OCE 结束迭代，由 TUI 显示 "(cancelled)"。
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new OrchestrationEvent.Error(ex.Message));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (TuiEventMapper.MapOrchestrationEventToTuiEvent(evt) is not { } mapped)
                continue;

            // Team's internal conversation remains in logs and delivery evidence.
            // The primary transcript receives only a replaceable user-facing projection.
            switch (mapped)
            {
                case TuiAgentCoordination coordination:
                    yield return new TuiModeProgress(
                        WorkingMode.Team,
                        $"正在协调 {coordination.FromName} 与 {coordination.ToName}…");
                    break;
                case TuiAgentMessage message:
                    yield return new TuiModeProgress(
                        WorkingMode.Team,
                        $"{message.AgentName} 已完成阶段工作…");
                    break;
                case TuiTextDelta:
                    break;
                case TuiTeamProgress progress:
                    yield return new TuiModeProgress(WorkingMode.Team, progress.Header);
                    break;
                default:
                    yield return mapped;
                    break;
            }
        }

        // Propagate any unhandled task-level exception (e.g. pre-start cancellation).
        await runTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 从 settings.json 加载 GOAL 模式三级预算配置。
    /// 配置项（用户可在 settings.json 中覆盖）：
    ///   - goal.maxSubGoalAttempts (int, default 20)
    ///   - goal.maxTotalTokens (long?, default 200000; null = 不限制)
    ///   - goal.maxWallClockHours (double?, default 2.0; null = 不限制)
    ///   - goal.maxCostUsd (decimal?, default 5.0; null = 不限制)
    /// </summary>
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

    private static BuildTerminalReason ResolveTerminalReason(GoalRunState state)
        => state switch
        {
            GoalRunState.Completed => BuildTerminalReason.Completed,
            GoalRunState.Paused => BuildTerminalReason.BudgetExceeded,
            GoalRunState.Cancelled => BuildTerminalReason.Cancelled,
            GoalRunState.Blocked => BuildTerminalReason.Blocked,
            GoalRunState.Failed => BuildTerminalReason.ValidationFailed,
            _ => BuildTerminalReason.AgentException,
        };

    private static GoalBudget BuildGoalBudgetFromSettings(IConfigManager configManager, int maxSubGoalAttempts)
    {
        var maxTotalTokens = configManager.Current.Effective.Get<long?>("goal.maxTotalTokens", 200_000);
        var maxWallClockHours = configManager.Current.Effective.Get<double?>("goal.maxWallClockHours", 2.0);
        var maxCostUsd = configManager.Current.Effective.Get<decimal?>("goal.maxCostUsd", 5.0m);

        return new GoalBudget
        {
            MaxSubGoalAttempts = maxSubGoalAttempts,
            MaxTotalTokens = maxTotalTokens,
            MaxWallClock = maxWallClockHours.HasValue ? TimeSpan.FromHours(maxWallClockHours.Value) : null,
            MaxCostUsd = maxCostUsd,
        };
    }
}
