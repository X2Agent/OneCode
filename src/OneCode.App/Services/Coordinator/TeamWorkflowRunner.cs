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
    /// ParallelDag mode: thin Sequential wrapper for exactly one assignee member.
    /// W3-B: never Concurrent true-parallel — product semantics stay single-member Sequential.
    /// Multi-member teams still plan branches separately; each branch run resolves one assignee.
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
        if (config.Members.Count == 0)
            throw new InvalidOperationException($"Team '{config.TeamName}' ParallelDag has no members.");

        var matches = config.Members
            .Where(m =>
                string.Equals(m.Role, task.AssigneeRole, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.AgentId, task.AssigneeRole, StringComparison.OrdinalIgnoreCase))
            .ToList();

        TeamMember member;
        if (matches.Count == 1)
        {
            member = matches[0];
        }
        else if (matches.Count == 0 && config.Members.Count == 1)
        {
            // Single-member team: AssigneeRole may be omitted; still Sequential, never Concurrent.
            member = config.Members[0];
        }
        else
        {
            throw new InvalidOperationException(
                $"ParallelDag requires exactly one assignee for task '{task.Id}' " +
                $"(AssigneeRole='{task.AssigneeRole}'); matched {matches.Count} of {config.Members.Count} members. " +
                "Do not use Concurrent true-parallel for ParallelDag.");
        }

        logger.LogInformation(
            "Team '{Name}' ParallelDag branch (Sequential thin wrapper): task={Task} member={Member}",
            config.TeamName, task.Id, member.AgentId);

        var agent = await agentFactory.BuildAgentAsync(
                member, transaction, cwd, eventSink, taskAllowedTools)
            .ConfigureAwait(false);

        // W3-B: Sequential thin wrapper — never ConcurrentWorkflowBuilder / true parallel.
        // Built explicitly rather than through SequentialWorkflowBuilder so the host options can
        // enable EmitAgentResponseEvents (the builder hard-codes its AIAgentHostOptions); approval
        // requests surface as external requests and are bridged by ExecuteWorkflowAsync.
        var workflow = BuildSequentialWorkflow(config.TeamName, [agent]);

        var inputMessage = BuildInputMessage(goal, imagePaths);
        var (result, sessionId) = await ExecuteWorkflowAsync(
            workflow, inputMessage, config.TeamName, "ParallelDag", config.MaxTurns, eventSink, ct)
            .ConfigureAwait(false);

        return new TeamRunResult(config.TeamName, result.FinalOutput, result.TurnsCompleted,
            result.MaxTurnsReached, result.InputTokens, result.OutputTokens,
            SessionId: sessionId, HadFailures: result.HadFailures);
    }


    /// <summary>
    /// W3-C: TeamRun-level approval already covered Magentic plan review — auto-approve once here.
    /// Do not add a second product plan-approval path.
    /// </summary>
    private async IAsyncEnumerable<WorkflowEvent> WatchWithHostDecisionsAsync(
        StreamingRun streamingRun,
        string teamName,
        IApprovalBroker approvalBroker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in streamingRun.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            await TryAutoApproveMagenticPlanReviewAsync(streamingRun, evt, teamName).ConfigureAwait(false);
            await BridgeToolApprovalAsync(streamingRun, evt, teamName, approvalBroker, ct).ConfigureAwait(false);
            yield return evt;
        }
    }

    private async Task TryAutoApproveMagenticPlanReviewAsync(
        StreamingRun streamingRun,
        WorkflowEvent evt,
        string teamName)
    {
        if (evt is not RequestInfoEvent { Request: { } pending } ||
            !pending.TryGetDataAs<MagenticPlanReviewRequest>(out var planReview) ||
            planReview is null)
        {
            return;
        }

        logger.LogInformation(
            "Auto-approving Magentic plan review for team '{Team}' (already approved at TeamRun level).",
            teamName);
        await streamingRun.SendResponseAsync(new ExternalResponse(
            pending.PortInfo,
            pending.RequestId,
            new PortableValue(planReview.Approve()))).ConfigureAwait(false);
    }

    /// <summary>
    /// R4 审批桥：把成员产生的 <see cref="ToolApprovalRequestContent"/> 外部请求接到产品审批事件流。
    ///
    /// <para><b>为什么必须桥接。</b> 成员 Agent 挂了 MAF 审批（Ask 决策 → 审批请求而非执行）。
    /// 工作流把该请求作为外部请求抛出（<see cref="RequestInfoEvent"/>）；若无人应答，
    /// 成员 turn 永久挂起、工作流停摆——审批请求绝不允许静默丢失，也绝不静默放行。</para>
    ///
    /// <para><b>映射语义</b>：AllowOnce / AllowAlways 均为单次批准（后者显式降级并留痕——原生
    /// AlwaysApprove 包装能否无损通过工作流端口未经验证，见方法内注释与 ADR 0007 §5.1），
    /// 其余按拒绝。取消传播为 <see cref="OperationCanceledException"/>（取消 ≠ 拒绝），
    /// broker 内部已对 UI 故障 fail-closed Deny。</para>
    ///
    /// <para><b>成员归属。</b> <see cref="RequestInfoEvent"/> 不携带产生请求的成员标识，
    /// 因此审批卡片的 AgentName 统一为团队名。</para>
    /// </summary>
    private async Task BridgeToolApprovalAsync(
        StreamingRun streamingRun,
        WorkflowEvent evt,
        string teamName,
        IApprovalBroker approvalBroker,
        CancellationToken ct)
    {
        if (evt is not RequestInfoEvent { Request: { } pending }
            || !pending.TryGetDataAs<ToolApprovalRequestContent>(out var approvalRequest)
            || approvalRequest is null)
        {
            return;
        }

        var functionCall = approvalRequest.ToolCall as FunctionCallContent;
        var toolName = functionCall?.Name ?? "unknown";
        var toolInput = functionCall?.Arguments is not null
            ? JsonSerializer.SerializeToElement(functionCall.Arguments)
            : JsonSerializer.SerializeToElement(new { });

        logger.LogInformation(
            "Bridging tool approval request for team '{Team}': tool={Tool} requestId={RequestId}",
            teamName, toolName, pending.RequestId);

        var decision = await approvalBroker.RequestAsync(
            new ApprovalRequest(
                RequestId: pending.RequestId,
                ToolName: toolName,
                ToolInput: toolInput.GetRawText()),
            ct).ConfigureAwait(false);

        // Team 桥当前只提供单次批准。原生「总是允许」依赖 AlwaysApproveToolApprovalResponseContent
        // 包装（直接继承 AIContent，非 ToolApprovalResponseContent 子类），能否无损通过工作流端口的
        // 响应类型校验并进入成员管线未经验证——审计 §4.7.2 明确不得静默降级，故显式降为单次并留痕。
        // 成员 session 随工作流运行销毁，standing rule 本就不跨 run 传播，实际损失有限。
        var approved = decision is ApprovalDecision.AllowOnce or ApprovalDecision.AllowAlways;
        if (decision == ApprovalDecision.AllowAlways)
        {
            logger.LogInformation(
                "Team bridge maps AllowAlways to a single approval (standing-rule wrapper not verified through the workflow port).");
        }

        var response = approvalRequest.CreateResponse(
            approved,
            approved ? $"User approved {toolName}" : "User denied.");

        await streamingRun.SendResponseAsync(
            pending.CreateResponse(response)).ConfigureAwait(false);
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
        var agents = new AIAgent[config.Members.Count];
        for (int i = 0; i < config.Members.Count; i++)
            agents[i] = await agentFactory.BuildAgentAsync(
                    config.Members[i], transaction, cwd, eventSink, taskAllowedTools)
                .ConfigureAwait(false);

        // 共识提前终止：预算过半后，若最后一条实质发言无新文本/工具活动，判定讨论已收敛。
        // 只提前、不延后——maxTurns 仍是硬上限。工具调用（FunctionCallContent）计入活动，
        // 避免纯工具轮被误判为共识（对应 MAF shouldTerminateFunc 传入的完整 chat history）。
        var consensusMinRounds = Math.Max(2, maxTurns / 2);
        var workflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agentList => new RoundRobinGroupChatManager(
                    agentList,
                    (manager, history, _) =>
                    {
                        // 硬上限：IterationCount 是已完成的迭代数（第 N 次检查时等于 N-1），>= maxTurns 即达轮数上限。
                        if (manager.IterationCount >= maxTurns)
                            return ValueTask.FromResult(true);

                        if (manager.IterationCount < consensusMinRounds)
                            return ValueTask.FromResult(false);

                        var lastAssistant = history.LastOrDefault(message => message.Role == ChatRole.Assistant);
                        var settled = lastAssistant is not null
                            && string.IsNullOrWhiteSpace(lastAssistant.Text)
                            && !lastAssistant.Contents.OfType<FunctionCallContent>().Any();
                        return ValueTask.FromResult(settled);
                    })
                    {
                        // MAF 基类默认 MaximumIterationCount = 40；不显式设置时 maxTurns > 40 会被静默截断。
                        MaximumIterationCount = Math.Max(1, maxTurns),
                    })
            .AddParticipants(agents)
            .WithName(config.TeamName)
            .Build();

        var inputMessage = BuildInputMessage(goal, imagePaths);
        var (result, sessionId) = await ExecuteWorkflowAsync(
            workflow, inputMessage, config.TeamName, "GroupChat", maxTurns, eventSink, ct)
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
    /// Builds a sequential workflow chaining the given member agents as bound executors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why manual binding.</b> <c>SequentialWorkflowBuilder</c> hard-codes its
    /// <see cref="AIAgentHostOptions"/> and exposes no way to set
    /// <see cref="AIAgentHostOptions.EmitAgentResponseEvents"/>, which the event processor needs
    /// for turn counting, so the executors are bound here. The topology is identical: members
    /// chained in order, workflow output taken from the last one.
    /// </para>
    /// <para>
    /// <b>Approval delivery.</b> <see cref="AIAgentHostOptions.InterceptUserInputRequests"/> stays
    /// off (the default): member approval requests surface as external requests
    /// (<see cref="RequestInfoEvent"/>) on the watch stream and are answered by
    /// <see cref="BridgeToolApprovalAsync"/>. Intercepting would send the request as a workflow
    /// message — which a single-member chain has no downstream executor to deliver to, silently
    /// dropping the request and stalling the member's turn.
    /// </para>
    /// </remarks>
    private static Workflow BuildSequentialWorkflow(string teamName, IReadOnlyList<AIAgent> agents)
    {
        var hostOptions = new AIAgentHostOptions
        {
            EmitAgentResponseEvents = true,
        };

        var bindings = agents.Select(agent => agent.BindAsExecutor(hostOptions)).ToList();
        var builder = new WorkflowBuilder(bindings[0]);
        for (var i = 1; i < bindings.Count; i++)
            builder.AddEdge(bindings[i - 1], bindings[i]);

        return builder
            .WithOutputFrom(bindings[^1])
            .WithName(teamName)
            .Build();
    }

    /// <summary>
    /// 统一的 Workflow 执行入口，封装 Mermaid 可视化和事件流处理。
    ///
    /// 提取此方法消除 GroupChat/Magentic/ParallelDag 三条路径的执行逻辑重复。
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
            // W3-C: Magentic plan review auto-approve is a single helper (TeamRun already approved).
            // R4: tool approvals raised by members surface as external requests on the same stream;
            // the broker bridges them to the product approval event stream and sends the decision back.
            var approvalBroker = ApprovalBroker.ForTeam(teamName, eventSink);
            result = await AgentWorkflowEventProcessor.ProcessStreamAsync(
                WatchWithHostDecisionsAsync(streamingRun, teamName, approvalBroker, ct),
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

        List<AIContent> contents = [];
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
