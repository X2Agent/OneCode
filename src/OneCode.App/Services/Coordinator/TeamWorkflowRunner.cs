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
    InProcessExecutionEnvironment? executionEnvironment = null)
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
            _ => RunGroupChatAsync(
                config, taskGoal, transaction, cwd, eventSink, ct, imagePaths, taskAllowedTools),
        };
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

        // Build agents sequentially — BuildAgentAsync may load coordinator prompt (shared cache)
        var agents = new AIAgent[config.Members.Count];
        for (int i = 0; i < config.Members.Count; i++)
            agents[i] = await agentFactory.BuildAgentAsync(
                    config.Members[i], transaction, cwd, eventSink, taskAllowedTools)
                .ConfigureAwait(false);

        var roundsRun = 0;
        var workflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agentList => new RoundRobinGroupChatManager(
                    agentList,
                    (_, _, _) => ValueTask.FromResult(roundsRun++ >= maxTurns)))
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
        var streamingRun = await env
            .RunStreamingAsync(workflow, inputMessage, sessionId, ct)
            .ConfigureAwait(false);

        AgentWorkflowEventProcessor.ProcessResult result;
        try
        {
            result = await AgentWorkflowEventProcessor.ProcessStreamAsync(
                streamingRun.WatchStreamAsync(ct),
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
