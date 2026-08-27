using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;
using TeamRunResult = OneCode.Core.Coordinator.TeamRunResult;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Task-level execution seam consumed by <see cref="TeamTaskWorkflowRuntime"/>.
/// Extracted so the runtime's out-of-scope attribution (C1) can be unit-tested
/// without building real MAF agents.
/// </summary>
internal interface ITeamTaskWorkflowRunner
{
    Task<TeamRunResult> RunTaskAsync(
        TeamConfig config,
        TeamTaskDefinition task,
        EditTransaction transaction,
        string cwd,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct,
        IReadOnlyList<string>? imagePaths = null);
}

/// <summary>
/// Workflow runner extracted from TeamOrchestrationService.
///
/// 职责：基于 MAF 的两种团队协调模式构建并执行工作流：
///   - GroupChat（<see cref="RoundRobinGroupChatManager"/>）：peer 协作，轮询发言。
///   - Magentic（<see cref="MagenticWorkflowBuilder"/>）：orchestrator 委派 worker。
///
/// Agent 构建委托给 <see cref="TeamAgentFactory"/>；流式事件处理委托给
/// <see cref="AgentWorkflowEventProcessor"/>。
///
/// 恢复模型（M5 后）：每个 Team 任务作为 Durable Workflow Host 中的单个幂等单元执行；
/// S-01 实测 GroupChat 无法从中间 Checkpoint 精确续跑，因此任务级工作流不再维护
/// 同进程 Checkpoint/Workflow 缓存——崩溃后由 TeamRun 业务聚合 + 新执行世代驱动重启。
/// </summary>
/// <param name="agentFactory">Team Agent 构建工厂。</param>
/// <param name="logger">日志记录器。</param>
/// <param name="executionEnvironment">
/// MAF 执行环境。为 null 时使用 <see cref="InProcessExecution.Default"/>（OffThread，生产模式）；
/// 测试可传入 <see cref="InProcessExecution.Lockstep"/> 获得确定性事件顺序。
/// </param>
internal sealed class TeamWorkflowRunner(
    TeamAgentFactory agentFactory,
    ILogger<TeamWorkflowRunner> logger,
    InProcessExecutionEnvironment? executionEnvironment = null) : ITeamTaskWorkflowRunner
{
    /// <summary>
    /// 暴露 AgentFactory 供外部复用（_rolePromptCache 需共享）。
    /// </summary>
    public TeamAgentFactory AgentFactory => agentFactory;

    /// <summary>
    /// 执行批准计划中的单个具体任务。MAF 仅负责该任务内部的角色协作；
    /// 任务依赖、状态持久化、质量门禁和事务提交仍由 TeamRun 控制面负责。
    /// </summary>
    public Task<TeamRunResult> RunTaskAsync(
        TeamConfig config,
        TeamTaskDefinition task,
        EditTransaction transaction,
        string cwd,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct,
        IReadOnlyList<string>? imagePaths = null)
    {
        var taskGoal = BuildTaskGoal(task);
        var taskAllowedTools = task.RequiredTools is { Count: > 0 }
            ? task.RequiredTools
            : task.ToolPolicy == TeamToolPolicy.ReadOnly
                ? PipelineProfileBehavior.ReadOnlyAgentTools
                : null;
        return config.Mode switch
        {
            TeamOrchestrationMode.Magentic => RunMagenticTeamAsync(
                config, taskGoal, transaction, cwd, eventSink, ct, imagePaths, taskAllowedTools),
            TeamOrchestrationMode.ParallelDag => RunParallelBranchAsync(
                config, task, taskGoal, transaction, cwd, eventSink, ct, imagePaths, taskAllowedTools),
            _ => RunGroupChatAsync(
                config, taskGoal, transaction, cwd, eventSink, ct, imagePaths, taskAllowedTools),
        };
    }

    /// <summary>
    /// ParallelDag 模式：任务只由其 AssigneeRole 对应的单个成员独立执行。
    /// 上下文隔离是此模式的核心价值——各分支互不可见，由聚合任务负责汇总。
    /// </summary>
    private async Task<TeamRunResult> RunParallelBranchAsync(
        TeamConfig config,
        TeamTaskDefinition task,
        string goal,
        EditTransaction transaction,
        string cwd,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct,
        IReadOnlyList<string>? imagePaths,
        IReadOnlyList<string>? taskAllowedTools)
    {
        var member = config.Members.FirstOrDefault(m =>
                string.Equals(m.Role, task.AssigneeRole, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.AgentId, task.AssigneeRole, StringComparison.OrdinalIgnoreCase))
            ?? config.Members[0];

        logger.LogInformation(
            "Team '{Name}' ParallelDag branch: task={Task} member={Member}",
            config.TeamName, task.Id, member.AgentId);

        var agent = await agentFactory.BuildAgentAsync(
                member, transaction, cwd, eventSink, taskAllowedTools)
            .ConfigureAwait(false);

        // 单成员顺序工作流：跑一次该成员即为本分支产出，无需群聊轮询或编排器。
        var workflow = new SequentialWorkflowBuilder([agent])
            .WithName(config.TeamName)
            .Build();

        var inputMessage = BuildInputMessage(goal, imagePaths);
        var (result, sessionId) = await ExecuteWorkflowAsync(
            workflow, inputMessage, config.TeamName, "ParallelDag", config.MaxTurns, eventSink, ct)
            .ConfigureAwait(false);

        return new TeamRunResult(config.TeamName, result.FinalOutput, result.TurnsCompleted,
            result.MaxTurnsReached, result.InputTokens, result.OutputTokens,
            SessionId: sessionId, HadFailures: result.HadFailures);
    }

    private static string BuildTaskGoal(TeamTaskDefinition task)
        => $"""
            Execute exactly one approved Team task. Do not create or reinterpret business tasks.
            Task id: {task.Id}
            Title: {task.Title}
            Kind: {task.Kind}
            Assignee role: {task.AssigneeRole}
            Tool policy: {task.ToolPolicy}
            Required tools: {string.Join(", ", task.RequiredTools ?? [])}
            Allowed paths: {string.Join(", ", task.AllowedPaths ?? [])}
            Dependencies are already satisfied by the TeamRun control plane.
            Acceptance criteria:
            {string.Join("\n", task.AcceptanceCriteria.Select(criterion => $"- {criterion}"))}
            Return concrete evidence for this task only. Do not decide transaction commit or declare the whole TeamRun successful.
            """;

    /// <summary>
    /// 运行 GroupChat 工作流（轮询协作）。
    /// </summary>
    public async Task<TeamRunResult> RunGroupChatAsync(
        TeamConfig config,
        string goal,
        EditTransaction transaction,
        string cwd,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct,
        IReadOnlyList<string>? imagePaths = null,
        IReadOnlyList<string>? taskAllowedTools = null)
    {
        var maxTurns = config.MaxTurns;
        var roundsRun = 0;
        var activity = new GroupChatActivityTracker();
        Action<OrchestrationEvent>? trackedSink = eventSink is null
            ? null
            : evt =>
            {
                // 发言（TextDelta）与工具活动都计入，避免纯工具轮被误判为共识。
                if (evt is OrchestrationEvent.TextDelta or OrchestrationEvent.ToolStart)
                    activity.OnActivity();
                eventSink(evt);
            };
        var agents = new AIAgent[config.Members.Count];
        for (int i = 0; i < config.Members.Count; i++)
            agents[i] = await agentFactory.BuildAgentAsync(
                    config.Members[i], transaction, cwd, trackedSink, taskAllowedTools)
                .ConfigureAwait(false);

        // 共识提前终止：预算过半后，若两次检查间无任何新发言/工具活动，判定讨论已收敛。
        // 只提前、不延后——maxTurns 仍是硬上限。保守阈值避免对纯工具轮误判。
        var consensusMinRounds = Math.Max(2, maxTurns / 2);
        var workflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agentList => new RoundRobinGroupChatManager(
                    agentList,
                    (_, _, _) =>
                    {
                        if (roundsRun++ >= maxTurns)
                            return ValueTask.FromResult(true);
                        return ValueTask.FromResult(
                            roundsRun > consensusMinRounds && activity.HasSettled());
                    }))
            .AddParticipants(agents)
            .WithName(config.TeamName)
            .Build();

        var inputMessage = BuildInputMessage(goal, imagePaths);
        var (result, sessionId) = await ExecuteWorkflowAsync(
            // R-1：workflow 层事件也走 trackedSink，与 agent pipeline 层共用同一活动统计源。
            workflow, inputMessage, config.TeamName, "GroupChat", maxTurns, trackedSink, ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Team '{Name}' GroupChat finished: turns={Turns} max={Max} len={Len} session={Session}",
            config.TeamName, result.TurnsCompleted, result.MaxTurnsReached, result.FinalOutput.Length, sessionId);

        return new TeamRunResult(config.TeamName, result.FinalOutput, result.TurnsCompleted,
            result.MaxTurnsReached, result.InputTokens, result.OutputTokens,
            SessionId: sessionId, HadFailures: result.HadFailures);
    }

    /// <summary>
    /// 运行 Magentic 工作流（orchestrator 委派）。
    /// </summary>
    public async Task<TeamRunResult> RunMagenticTeamAsync(
        TeamConfig config,
        string goal,
        EditTransaction transaction,
        string cwd,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct,
        IReadOnlyList<string>? imagePaths = null,
        IReadOnlyList<string>? taskAllowedTools = null)
    {
        var maxTurns = config.MaxTurns;

        // 按 role 查找 orchestrator（"orchestrator" 或 "lead"），而非假设 Members[0]。
        // 这与 TeamOrchestrationService.RunTeamStreamingAsync 中的 orchestrator 查找逻辑一致，
        // 确保 YAML 中 orchestrator 不在首位时也能正确识别。
        var orchestratorIndex = config.Members
            .Select((m, i) => (m, i))
            .FirstOrDefault(t => t.m.Role is "orchestrator" or "lead").i;
        if (orchestratorIndex < 0)
            orchestratorIndex = 0; // 兜底：无 orchestrator 角色时用第一个成员

        var orchestratorMember = config.Members[orchestratorIndex];
        var orchestrator = await agentFactory.BuildAgentAsync(
                orchestratorMember, transaction, cwd, eventSink, taskAllowedTools)
            .ConfigureAwait(false);

        // 其余成员作为 workers
        var workers = new AIAgent[config.Members.Count - 1];
        var wi = 0;
        for (int i = 0; i < config.Members.Count; i++)
        {
            if (i == orchestratorIndex) continue;
            workers[wi] = await agentFactory.BuildAgentAsync(
                    config.Members[i], transaction, cwd, eventSink, taskAllowedTools)
                .ConfigureAwait(false);
            wi++;
        }

        if (workers.Length == 0)
        {
            logger.LogWarning(
                "Team '{Name}' Magentic mode requires at least 2 members (orch+workers); falling back to GroupChat",
                config.TeamName);
            return await RunGroupChatAsync(
                config, goal, transaction, cwd, eventSink, ct, imagePaths, taskAllowedTools)
                .ConfigureAwait(false);
        }

        var workflow = new MagenticWorkflowBuilder(orchestrator)
            .AddParticipants(workers)
            .WithMaxRounds(maxTurns)
            .Build();

        var inputMessage = BuildInputMessage(goal, imagePaths);
        var (result, sessionId) = await ExecuteWorkflowAsync(
            workflow, inputMessage, config.TeamName, "Magentic", maxTurns, eventSink, ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Team '{Name}' Magentic finished: turns={Turns} max={Max} len={Len} session={Session}",
            config.TeamName, result.TurnsCompleted, result.MaxTurnsReached, result.FinalOutput.Length, sessionId);

        return new TeamRunResult(config.TeamName, result.FinalOutput, result.TurnsCompleted,
            result.MaxTurnsReached, result.InputTokens, result.OutputTokens,
            SessionId: sessionId, HadFailures: result.HadFailures);
    }

    /// <summary>
    /// 统一的 Workflow 执行入口，封装 Mermaid 可视化和事件流处理。
    ///
    /// 提取此方法消除 GroupChat/Magentic 两条路径的执行逻辑重复。
    /// 任务级工作流是一次性幂等单元：不再维护 Checkpoint/会话缓存，
    /// 崩溃恢复由 TeamRun 业务聚合 + Durable Workflow Host 新世代负责。
    /// </summary>
    /// <returns>执行结果和本次 MAF 运行 ID。</returns>
    private async Task<(AgentWorkflowEventProcessor.ProcessResult Result, SessionId SessionId)> ExecuteWorkflowAsync(
        Workflow workflow,
        ChatMessage inputMessage,
        string teamName,
        string modeName,
        int maxTurns,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        // Workflow Mermaid 可视化（Debug 级别输出拓扑图）
        if (logger.IsEnabled(LogLevel.Debug))
        {
            try
            {
                var mermaid = WorkflowVisualizer.ToMermaidString(workflow);
                logger.LogDebug(
                    "Team '{Name}' {Mode} workflow topology:\n{Mermaid}",
                    teamName, modeName, mermaid);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate Mermaid visualization for team '{Name}'", teamName);
            }
        }

        var sessionId = SessionId.NewId();
        var env = executionEnvironment ?? InProcessExecution.Default;
        logger.LogInformation(
            "Team workflow START team={Team} mode={Mode} ctCancelled={Cancelled} sessionId={Session}",
            teamName, modeName, ct.IsCancellationRequested, sessionId);

        // ChatProtocol 语义（MAF 1.19.0）：agent executor 收到普通 ChatMessage 只累积对话，
        // 必须收到 TurnToken 才会执行（TakeTurnAsync）。RunStreamingAsync 直接投递消息对
        // Sequential（ParallelDag 分支）与 GroupChat 均表现为 executor 瞬间完成——零 LLM 调用、
        // turns=0、"(no output)"（已由 MagenticReproTests 复现验证）。
        // 统一采用两段式启动（与 MAF 官方 probe 一致）：OpenStreamingAsync → 发任务消息 → 发 TurnToken。
        // Magentic 额外原因：orchestrator（ChatProtocolExecutor）配置了 AutoSendTurnToken=false，
        // 直接投递会永久等待 TurnToken 而挂起。
        StreamingRun streamingRun = await env
            .OpenStreamingAsync(workflow, sessionId, ct)
            .ConfigureAwait(false);
        _ = await streamingRun.TrySendMessageAsync(inputMessage).ConfigureAwait(false);
        _ = await streamingRun.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

        AgentWorkflowEventProcessor.ProcessResult result;
        try
        {
            // 自动批准 Magentic 计划评审：Magentic orchestrator 在创建计划后会通过
            // RequestPort 等待人工签核（MagenticPlanReviewRequest）。TEAM 模式的计划审批
            // 已在 TeamRun 控制面（TeamApprovalWorkflow）由用户完成，这里无需二次签核，
            // 收到请求即自动批准，避免工作流无限挂起。
            async IAsyncEnumerable<WorkflowEvent> WatchWithAutoApprovalAsync()
            {
                await foreach (var evt in streamingRun.WatchStreamAsync(ct).ConfigureAwait(false))
                {
                    if (evt is RequestInfoEvent { Request: { } pending } &&
                        pending.TryGetDataAs<MagenticPlanReviewRequest>(out var planReview) &&
                        planReview is not null)
                    {
                        logger.LogInformation(
                            "Auto-approving Magentic plan review for team '{Team}' (already approved at TeamRun level).",
                            teamName);
                        await streamingRun.SendResponseAsync(new ExternalResponse(
                            pending.PortInfo,
                            pending.RequestId,
                            new PortableValue(planReview.Approve()))).ConfigureAwait(false);
                    }

                    yield return evt;
                }
            }

            result = await AgentWorkflowEventProcessor.ProcessStreamAsync(
                WatchWithAutoApprovalAsync(),
                maxTurns,
                "Team '{Name}' {Mode} member failed: {Error}",
                [teamName, modeName],
                logger,
                eventSink,
                ct).ConfigureAwait(false);
        }
        finally
        {
            await streamingRun.DisposeAsync().ConfigureAwait(false);
        }

        return (result, sessionId);
    }


    /// <summary>
    /// Builds the initial user ChatMessage, attaching images as DataContent when present.
    /// </summary>
    private ChatMessage BuildInputMessage(string goal, IReadOnlyList<string>? imagePaths)
    {
        if (imagePaths is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, goal);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(goal))
            contents.Add(new TextContent(goal));

        foreach (var path in imagePaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                contents.Add(new DataContent(bytes, mediaType));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load team image attachment {Path}", path);
                contents.Add(new TextContent($"[Failed to load image: {Path.GetFileName(path)}]"));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }
}

/// <summary>
/// GroupChat 共识终止的活动计数器：统计成员发言与工具活动总数，
/// 两次终止检查之间计数无增长即判定讨论已收敛。
/// </summary>
internal sealed class GroupChatActivityTracker
{
    private readonly object _gate = new();
    private long _total;
    private long _seenAtLastCheck;

    public void OnActivity()
    {
        lock (_gate) _total++;
    }

    /// <summary>自上次检查无新活动返回 true；从未有过活动时不允许判收敛。</summary>
    public bool HasSettled()
    {
        lock (_gate)
        {
            if (_total == 0) return false;
            var settled = _total == _seenAtLastCheck;
            _seenAtLastCheck = _total;
            return settled;
        }
    }
}