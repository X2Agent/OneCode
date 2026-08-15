using Microsoft.Agents.AI.Workflows;
using OneCode.Core.Coordinator;
using OneCode.App.Services.Agent;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Team orchestration service using MAF's formal Team abstractions.
///
/// Two MAF team patterns:
///   1. GroupChat (RoundRobinGroupChatManager): peer collaboration with turn-based chat.
///      Each agent (role) speaks in order; used when template="groupchat" or unspecified.
///   2. Magentic (MagenticWorkflowBuilder): orchestrator-led delegation to workers.
///      Used when template="magentic-orchestrator" in the team YAML.
///
/// Team config: YAML files at ~/.onecode/teams/{name}/team.yaml
/// using the same AgentTemplateConfig format as sub-agent templates.
///
/// Agent 构建、工作流运行、审批事件映射已分别提取到
/// <see cref="TeamAgentFactory"/>、<see cref="TeamWorkflowRunner"/>，审批映射内联到 TeamAgentFactory 中。
/// </summary>
public sealed class TeamOrchestrationService
    : ITeamOrchestrationService, IDisposable
{
    private readonly TeamWorkflowRunner _workflowRunner;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TeamOrchestrationService> _logger;
    private readonly TeamRunApplicationService _teamRunService;
    private readonly TeamRequirementService _requirementService;
    private readonly IClarificationInteractionService _clarificationInteraction;
    private readonly IWorkingDirectoryAccessor _workingDirectoryAccessor;
    private readonly TeamTaskWorkflowHost _taskWorkflowHost;
    private readonly TeamApprovalWorkflowHost _approvalWorkflowHost;
    private readonly TeamClarificationWorkflowHost _clarificationWorkflowHost;
    private readonly ITeamRunStore _teamRunStore;
    private readonly OneCode.Core.Workflows.IOperationLedger? _operationLedger;

    private readonly ConcurrentDictionary<string, TeamConfig> _teams = new(StringComparer.OrdinalIgnoreCase);

    internal TeamOrchestrationService(
        TeamWorkflowRunner workflowRunner,
        ILoggerFactory loggerFactory,
        ILogger<TeamOrchestrationService> logger,
        TeamRunApplicationService teamRunService,
        TeamRequirementService requirementService,
        IClarificationInteractionService clarificationInteraction,
        IWorkingDirectoryAccessor workingDirectoryAccessor,
        TeamTaskWorkflowHost taskWorkflowHost,
        TeamApprovalWorkflowHost approvalWorkflowHost,
        TeamClarificationWorkflowHost clarificationWorkflowHost,
        ITeamRunStore teamRunStore,
        OneCode.Core.Workflows.IOperationLedger? operationLedger = null)
    {
        _workflowRunner = workflowRunner;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _teamRunService = teamRunService;
        _requirementService = requirementService;
        _clarificationInteraction = clarificationInteraction;
        _workingDirectoryAccessor = workingDirectoryAccessor;
        _taskWorkflowHost = taskWorkflowHost;
        _approvalWorkflowHost = approvalWorkflowHost;
        _clarificationWorkflowHost = clarificationWorkflowHost;
        _teamRunStore = teamRunStore;
        _operationLedger = operationLedger;
    }

    public IReadOnlyList<string> RegisteredTeams =>
        _teams.Keys.OrderBy(k => k).ToList();

    /// <summary>当前活跃团队。为 null 时回退到第一个注册的团队。</summary>
    public string? ActiveTeam { get; set; }

    /// <summary>获取当前应使用的团队名（ActiveTeam 或第一个注册的团队）</summary>
    public string? ResolveActiveTeam()
    {
        if (!string.IsNullOrEmpty(ActiveTeam) && _teams.ContainsKey(ActiveTeam))
            return ActiveTeam;
        var teams = RegisteredTeams;
        return teams.Count > 0 ? teams[0] : null;
    }

    public string? GetTeamMode(string teamName) =>
        _teams.TryGetValue(teamName, out var config)
            ? config.Mode == TeamOrchestrationMode.Magentic ? "magentic" : "groupchat"
            : null;

    /// <summary>
    /// 返回指定团队的成员信息列表（AgentId + Role + 是否为 Orchestrator）。
    /// 用于 TUI 启动横幅显示成员构成，让用户知道这个团队有哪些角色。
    /// </summary>
    public IReadOnlyList<TeamMemberInfo>? GetTeamMembers(string teamName) =>
        _teams.TryGetValue(teamName, out var config)
            ? config.Members
                .Select(m => new TeamMemberInfo(
                    m.AgentId,
                    m.Role,
                    m.Role is "orchestrator" or "lead"))
                .ToList()
                .AsReadOnly()
            : null;

    public async Task RegisterTeamAsync(
        string teamName,
        string teamFilePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamFilePath) || !File.Exists(teamFilePath))
        {
            _logger.LogWarning("Team file not found for '{TeamName}': {Path}", teamName, teamFilePath);
            return;
        }

        try
        {
            // 统一 YAML 格式，不再支持 JSON。
            var config = TeamConfigLoader.LoadTeamFromYaml(teamFilePath, teamName);
            _teams[teamName] = config;
            _logger.LogInformation(
                "Team '{TeamName}' registered: mode={Mode} members={Count}",
                teamName, config.Mode, config.Members.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register team '{TeamName}'", teamName);
        }
    }

    public Task UnregisterTeamAsync(string teamName, CancellationToken ct = default)
    {
        _teams.TryRemove(teamName, out _);
        _logger.LogInformation("Team '{TeamName}' unregistered", teamName);
        return Task.CompletedTask;
    }

    // 内置团队模板：从嵌入式资源加载（OneCode.App.prompts.teams.{name}.yaml）

    private static readonly string[] BuiltinTeamTemplates = ["feature-impl", "code-review", "research"];

    /// <summary>
    /// 注册内置团队模板（从嵌入式资源加载）。
    /// 幂等：已注册的同名团队不会被覆盖。
    /// </summary>
    public Task RegisterBuiltinTeamsAsync(CancellationToken ct = default)
    {
        var assembly = typeof(TeamOrchestrationService).Assembly;

        foreach (var name in BuiltinTeamTemplates)
        {
            if (_teams.ContainsKey(name))
                continue;

            var resourceName = $"OneCode.App.prompts.teams.{name}.yaml";
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    _logger.LogWarning("Built-in team template resource not found: {Resource}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                var template = AgentTemplateConfig.FromYaml(yaml);

                var config = TeamConfigLoader.BuildTeamConfigFromTemplate(template, name);
                _teams[name] = config;
                _logger.LogInformation(
                    "Built-in team '{Name}' registered: mode={Mode} members={Count}",
                    name, config.Mode, config.Members.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load built-in team template: {Resource}", resourceName);
            }
        }

        // 2. 扫描用户团队目录（~/.onecode/teams/*/team.yaml）
        //    使 /team list 能显示用户自定义团队，无需先触发一次查询才注册。
        foreach (var (name, filePath) in TeamConfigLoader.DiscoverUserTeams())
        {
            if (_teams.ContainsKey(name))
                continue;

            try
            {
                var config = TeamConfigLoader.LoadTeamFromYaml(filePath, name);
                _teams[name] = config;
                _logger.LogInformation(
                    "User team '{Name}' registered from {Path}: mode={Mode} members={Count}",
                    name, filePath, config.Mode, config.Members.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user team '{Name}' from {Path}", name, filePath);
            }
        }

        if (string.IsNullOrEmpty(ActiveTeam) && _teams.ContainsKey("feature-impl"))
            ActiveTeam = "feature-impl";

        return Task.CompletedTask;
    }

    /// <summary>
    /// 流式运行 Team — 通过 <paramref name="eventSink"/> 回调实时推送 OrchestrationEvent 给 TUI 层。
    /// eventSink 为 null 时仅返回最终输出。
    /// </summary>
    public async Task<TeamRunResult> RunTeamStreamingAsync(
        string teamName,
        string goal,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct = default,
        TeamOrchestrationMode? overrideMode = null,
        IReadOnlyList<string>? imagePaths = null,
        SessionId? sessionId = null)
    {
        var (found, config) = await TryResolveTeamAsync(teamName, eventSink, ct).ConfigureAwait(false);
        if (!found || config is null)
        {
            return TeamError(teamName, $"Team '{teamName}' not found.", eventSink);
        }

        // 运行时覆盖编排模式 — TUI 切换 Magentic/GroupChat 时传入 overrideMode，
        // 覆盖 YAML 模板中 template 字段的默认值，使用户在 TUI 中切换策略能真正生效。
        if (overrideMode is { } mode)
        {
            config = config with { Mode = mode };
        }

        _logger.LogInformation(
            "Team '{TeamName}' streaming starting: mode={Mode} goal={Goal}",
            teamName, config.Mode, goal[..Math.Min(80, goal.Length)]);

        var orchestrator = config.Members.FirstOrDefault(m =>
            m.Role is "lead" or "orchestrator") ?? config.Members[0];
        eventSink?.Invoke(new OrchestrationEvent.AgentCoordination(
            CoreConstants.MessageTypes.User, null, orchestrator.AgentId, null, goal));

        var cwd = _workingDirectoryAccessor.WorkingDirectory;
        _logger.LogDebug("Team '{TeamName}' using working directory {WorkingDirectory}", teamName, cwd);
        var (fileChanges, observedSink) = CreateObservedSink(eventSink);
        var modelId = _workflowRunner.AgentFactory.MainModelId ?? "team-model";

        var runId = TeamRunId.NewId();
        var effectiveGoal = goal;
        RequirementAnalysisResult analysis;
        try
        {
            analysis = await _requirementService.AnalyzeAsync(effectiveGoal, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TeamError(teamName, $"需求澄清生成失败：{ex.Message}", eventSink);
        }
        var clarificationRunCreated = false;
        if (!analysis.CanProceedWithoutClarification)
        {
            var questions = analysis.Questions
                .Where(question => question.Blocking)
                .Select(question => question.Question)
                .ToList();
            _ = await _teamRunService.BeginClarificationAsync(
                runId, teamName, goal, cwd, questions, ct, sessionId).ConfigureAwait(false);
            clarificationRunCreated = true;

            var clarification = await RunClarificationGateAsync(
                teamName, runId, config, modelId, questions, goal, eventSink, ct).ConfigureAwait(false);
            if (clarification.Answer is null)
            {
                return TeamError(
                    teamName,
                    "Team request was cancelled during clarification; no workflow or write transaction was started.",
                    eventSink);
            }

            effectiveGoal = $"{goal}\nClarification response:\n{clarification.Answer}";
            try
            {
                analysis = await _requirementService.AnalyzeAsync(effectiveGoal, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TeamError(teamName, $"需求澄清生成失败：{ex.Message}", eventSink);
            }
            if (!analysis.CanProceedWithoutClarification)
            {
                return TeamError(
                    teamName,
                    "Team request is still ambiguous after clarification; no workflow or write transaction was started.",
                    eventSink);
            }
        }

        var plan = _requirementService.CreateImplementationPlan(analysis);

        // Persist the product run before creating the durable approval checkpoint.
        // Recovery must always have a TeamRun aggregate to bind to the workflow record.
        if (clarificationRunCreated)
        {
            _ = await _teamRunService.PromoteClarificationToApprovalAsync(
                runId, effectiveGoal, plan, ct).ConfigureAwait(false);
        }
        else
        {
            _ = await _teamRunService.BeginApprovalAsync(
                runId, teamName, effectiveGoal, cwd, plan, ct, sessionId).ConfigureAwait(false);
        }

        // Plan approval via durable MAF RequestPort workflow (survives process restart).
        var approval = await RunApprovalGateAsync(
            teamName, runId, config, modelId, plan, eventSink, ct).ConfigureAwait(false);
        if (approval.ApprovalGranted != true)
        {
            return TeamError(
                teamName,
                "Team plan was not approved; no write transaction was created.",
                eventSink);
        }

        var teamRun = await _teamRunService.BeginApprovedExecutionAsync(
            runId, teamName, effectiveGoal, cwd, plan, ct, sessionId).ConfigureAwait(false);

        try
        {
            return await ExecuteTeamWorkflowCoreAsync(
                teamRun, config, modelId, cwd, imagePaths, fileChanges, observedSink, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Team '{TeamName}' streaming workflow failed", teamName);
            var problem = AgentProblemDetails.ToolExecutionFailed(
                $"Team workflow error: {ex.Message}", toolName: "TeamOrchestration");
            eventSink?.Invoke(new OrchestrationEvent.Error(problem.Detail, problem));
            return new TeamRunResult(teamName, problem.Detail, 0, false, Error: problem);
        }
    }

    /// <summary>
    /// 恢复指定会话的 Team 执行（流式）。
    /// 通过共享 Durable Workflow Host 开启新执行世代：已完成任务的业务事实来自 TeamRun 聚合，
    /// 运行中任务按新 Attempt 重启，不恢复 MAF 内部中间游标。
    /// </summary>
    public async Task<TeamRunResult> ResumeTeamStreamingAsync(
        SessionId sessionId,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct = default)
    {
        var active = await _teamRunStore.ListActiveAsync(ct).ConfigureAwait(false);
        var teamRun = active.FirstOrDefault(run => run.SessionId == sessionId);
        if (teamRun is null || teamRun.Status is not (
                TeamRunStatus.Running or TeamRunStatus.Blocked or TeamRunStatus.WaitingForUser))
        {
            return TeamError(sessionId,
                $"No resumable TeamRun exists for session '{sessionId}'.",
                eventSink);
        }

        var (found, config) = await TryResolveTeamAsync(teamRun.TeamName, eventSink, ct).ConfigureAwait(false);
        if (!found || config is null)
        {
            return TeamError(teamRun.TeamName, $"Team '{teamRun.TeamName}' not found.", eventSink);
        }

        if (teamRun.Status == TeamRunStatus.WaitingForUser)
        {
            var modelId = _workflowRunner.AgentFactory.MainModelId ?? "team-model";
            if (teamRun.Phase == TeamRunPhase.Clarification)
            {
                var questions = teamRun.Requirements?.OpenQuestions ?? [];
                var clarification = await RunClarificationGateAsync(
                    teamRun.TeamName, teamRun.Id, config, modelId, questions,
                    teamRun.OriginalRequest, eventSink, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(clarification.Answer))
                    return TeamError(teamRun.TeamName, "Team clarification was cancelled.", eventSink);
                var clarifiedGoal = $"{teamRun.OriginalRequest}\nClarification response:\n{clarification.Answer}";
                RequirementAnalysisResult analysis;
                try
                {
                    analysis = await _requirementService.AnalyzeAsync(clarifiedGoal, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return TeamError(teamRun.TeamName, $"需求澄清生成失败：{ex.Message}", eventSink);
                }
                if (!analysis.CanProceedWithoutClarification)
                    return TeamError(teamRun.TeamName, "Team request remains ambiguous.", eventSink);
                var clarifiedPlan = _requirementService.CreateImplementationPlan(analysis);
                teamRun = await _teamRunService.PromoteClarificationToApprovalAsync(
                    teamRun.Id, clarifiedGoal, clarifiedPlan, ct).ConfigureAwait(false);
            }

            var plan = teamRun.Plan
                ?? throw new InvalidOperationException($"TeamRun '{teamRun.Id}' has no approval plan.");
            var approval = await RunApprovalGateAsync(
                teamRun.TeamName, teamRun.Id, config, modelId, plan, eventSink, ct).ConfigureAwait(false);
            if (approval.ApprovalGranted != true)
                return TeamError(teamRun.TeamName, "Team plan was not approved.", eventSink);

            teamRun = await _teamRunService.BeginApprovedExecutionAsync(
                teamRun.Id, teamRun.TeamName, teamRun.OriginalRequest,
                teamRun.WorkingDirectory, plan, ct, teamRun.SessionId).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Resuming team session {Session} as a new execution generation (run {RunId})",
            sessionId, teamRun.Id);

        var (fileChanges, observedSink) = CreateObservedSink(eventSink);

        try
        {
            var modelId = _workflowRunner.AgentFactory.MainModelId ?? "team-model";
            return await ExecuteTeamWorkflowCoreAsync(
                teamRun, config, modelId, teamRun.WorkingDirectory,
                imagePaths: null, fileChanges, observedSink, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Team resume failed for session {Session}", sessionId);
            var problem = AgentProblemDetails.ToolExecutionFailed(
                $"Team resume error: {ex.Message}", toolName: "TeamOrchestration");
            eventSink?.Invoke(new OrchestrationEvent.Error(problem.Detail, problem));
            return new TeamRunResult(sessionId, problem.Detail, 0, false, Error: problem);
        }
    }

    /// <summary>
    /// 释放资源。TeamWorkflowRunner 在 M5 后不再持有进程内 Checkpoint 缓存
    /// （恢复由 Durable Workflow Host + TeamRun 聚合负责），故此处为空实现以保持 IDisposable。
    /// </summary>
    public void Dispose()
    {
    }

    // --- 共享私有方法（RunTeamStreamingAsync 与 ResumeTeamStreamingAsync 复用） ---

    /// <summary>
    /// 创建一个包装 eventSink 的观察器，捕获 FileChanged 事件并累积到返回的 fileChanges 列表。
    /// 两个流式入口共用此逻辑。
    /// </summary>
    private static (List<OneCode.Core.Domain.FileChange> FileChanges, Action<OrchestrationEvent>? ObservedSink)
        CreateObservedSink(Action<OrchestrationEvent>? eventSink)
    {
        var fileChanges = new List<OneCode.Core.Domain.FileChange>();
        Action<OrchestrationEvent>? observedSink = evt =>
        {
            if (evt is OrchestrationEvent.FileChanged changed)
            {
                fileChanges.Add(new OneCode.Core.Domain.FileChange(
                    changed.FileName,
                    changed.AddedLines,
                    changed.RemovedLines));
            }
            eventSink?.Invoke(evt);
        };
        return (fileChanges, observedSink);
    }

    /// <summary>
    /// 构造 MAF ExternalResponse 以恢复 Team 澄清工作流（RequestPort 回答投递）。
    /// </summary>
    private static ExternalResponse BuildClarificationResponse(
        string portId, string requestId, string answerText) =>
        new(
            new Microsoft.Agents.AI.Workflows.Checkpointing.RequestPortInfo(
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamClarificationInput)),
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamClarificationResponse)),
                portId),
            requestId,
            new Microsoft.Agents.AI.Workflows.PortableValue(
                new TeamClarificationResponse(answerText)));

    /// <summary>
    /// 构造 MAF ExternalResponse 以恢复 Team 计划审批工作流（RequestPort 决策投递）。
    /// </summary>
    private static ExternalResponse BuildApprovalResponse(
        string portId, string requestId, bool approved) =>
        new(
            new Microsoft.Agents.AI.Workflows.Checkpointing.RequestPortInfo(
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamPlanApprovalInput)),
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamPlanApprovalDecision)),
                portId),
            requestId,
            new Microsoft.Agents.AI.Workflows.PortableValue(
                new TeamPlanApprovalDecision(approved)));

    /// <summary>
    /// 运行 Team 澄清门禁：首次调用挂起于 MAF RequestPort，通过 AskAsync 获取用户回答后投递
    /// ExternalResponse 恢复。返回包含 Answer 的最终结果；调用方负责判断 Answer 是否为空。
    /// </summary>
    private async Task<TeamClarificationResult> RunClarificationGateAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        IReadOnlyList<string> questions,
        string goalForEvent,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        var clarificationInput = new TeamClarificationInput(runId.Value, teamName, questions);
        var clarification = await _clarificationWorkflowHost.RunAsync(
            teamName, runId, config, modelId, clarificationInput,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);

        if (clarification.PendingRequest is { } pending)
        {
            eventSink?.Invoke(new OrchestrationEvent.TeamClarificationRequest(
                runId, teamName, goalForEvent, questions));
            var answer = await _clarificationInteraction.AskAsync(
                "团队任务需要补充信息", questions, ct: ct).ConfigureAwait(false);
            var response = BuildClarificationResponse(
                pending.PortId, pending.RequestId, answer.Response ?? string.Empty);
            clarification = await _clarificationWorkflowHost.RunAsync(
                teamName, runId, config, modelId, clarificationInput,
                new JsonSerializerOptions(), response, ct).ConfigureAwait(false);
        }

        return clarification;
    }

    /// <summary>
    /// 运行 Team 计划审批门禁：构造 approvalInput，首次调用挂起于 MAF RequestPort，
    /// 通过 AskAsync 获取用户审批决策后投递 ExternalResponse 恢复。
    /// 返回包含 ApprovalGranted 的最终结果；调用方负责判断是否批准。
    /// </summary>
    private async Task<TeamApprovalWorkflowResult> RunApprovalGateAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        ImplementationPlan plan,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        var approvalInput = new TeamPlanApprovalInput(
            runId.Value,
            teamName,
            plan.Summary,
            plan.Tasks.Select(t => t.Title).ToList(),
            plan.RequiredGates.Where(g => g.Required).Select(g => g.Description).ToList());

        var approval = await _approvalWorkflowHost.RunApprovalAsync(
            teamName, runId, config, modelId, approvalInput,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);

        if (approval.PendingRequest is { } pending)
        {
            // Notify TUI of plan approval card (display-only, no TaskCompletionSource).
            eventSink?.Invoke(new OrchestrationEvent.TeamPlanApprovalRequest(
                runId, teamName, plan.Summary,
                plan.Tasks.Select(t => t.Title).ToList(),
                plan.RequiredGates.Where(g => g.Required).Select(g => g.Description).ToList()));

            var decision = await _clarificationInteraction.AskAsync(
                $"团队 {teamName} 计划审批",
                [$"执行方案：{plan.Summary}\n任务数：{plan.Tasks.Count}\n批准执行？"],
                confirmationOnly: true,
                ct: ct).ConfigureAwait(false);
            var approved = !decision.IsCancelled;

            var response = BuildApprovalResponse(pending.PortId, pending.RequestId, approved);
            approval = await _approvalWorkflowHost.RunApprovalAsync(
                teamName, runId, config, modelId, approvalInput,
                new JsonSerializerOptions(),
                externalResponse: response,
                ct: ct).ConfigureAwait(false);
        }

        return approval;
    }

    /// <summary>
    /// 执行 Team 任务工作流的核心逻辑：构造 runtime、运行任务 DAG、聚合结果、完成业务事务。
    /// catch 块由调用方保留（Run 与 Resume 的错误文案不同）。
    /// </summary>
    private async Task<TeamRunResult> ExecuteTeamWorkflowCoreAsync(
        TeamRun teamRun,
        TeamConfig config,
        string modelId,
        string workingDirectory,
        IReadOnlyList<string>? imagePaths,
        List<OneCode.Core.Domain.FileChange> fileChanges,
        Action<OrchestrationEvent>? observedSink,
        CancellationToken ct)
    {
        using var runtime = new TeamTaskWorkflowRuntime(
            _teamRunService,
            _workflowRunner,
            config,
            workingDirectory,
            observedSink,
            imagePaths,
            static () => new EditTransaction(),
            _operationLedger);
        var workflowResult = await _taskWorkflowHost.RunNextAsync(
            teamRun, config, modelId, runtime,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);
        var result = BuildTeamResult(teamRun.TeamName, workflowResult.Outcomes);
        var bound = runtime.BoundRun;
        teamRun = await _teamRunService.CompleteExecutionAsync(
            bound, result, runtime.Transaction, fileChanges,
            runtime.FencingToken, ct, _operationLedger, runtime.RunOperationId).ConfigureAwait(false);
        await _taskWorkflowHost.CompleteBusinessAsync(
            teamRun.Id, runtime.FencingToken,
            teamRun.Status == TeamRunStatus.Succeeded
                ? OneCode.Core.Workflows.WorkflowRunState.Completed
                : OneCode.Core.Workflows.WorkflowRunState.Failed,
            ct).ConfigureAwait(false);
        return result with
        {
            RunId = teamRun.Id,
            Status = teamRun.Status,
            Delivery = teamRun.Delivery,
            HadFailures = teamRun.Status != TeamRunStatus.Succeeded,
        };
    }

    /// <summary>
    /// 将 MAF Team DAG 各任务的结构化结果聚合为业务 TeamRunResult。
    /// 任一 Required 任务失败/阻塞/取消，或上游失败导致下游 Blocked，均视为整体失败。
    /// </summary>
    private static TeamRunResult BuildTeamResult(
        string teamName,
        IReadOnlyList<TeamTaskOutcome> outcomes)
    {
        var failed = outcomes
            .Where(outcome => outcome.Status is
                TeamTaskOutcomeStatus.Failed or
                TeamTaskOutcomeStatus.Blocked or
                TeamTaskOutcomeStatus.Cancelled)
            .ToList();
        var succeeded = outcomes
            .Where(outcome => outcome.Status == TeamTaskOutcomeStatus.Succeeded)
            .ToList();

        var hadFailures = failed.Count > 0;
        var errorDetail = string.Join(
            "; ",
            failed.Where(outcome => !string.IsNullOrWhiteSpace(outcome.Error))
                .Select(outcome => $"{outcome.TaskId}: {outcome.Error}"));
        var error = hadFailures && !string.IsNullOrWhiteSpace(errorDetail)
            ? AgentProblemDetails.ToolExecutionFailed(errorDetail, toolName: "TeamOrchestration")
            : null;

        var summary = succeeded.Count > 0
            ? string.Join(
                "\n",
                succeeded.Where(outcome => !string.IsNullOrWhiteSpace(outcome.Summary))
                    .Select(outcome => outcome.Summary))
            : hadFailures
                ? (error?.Detail ?? "Team task execution failed.")
                : "No Team task produced output.";

        return new TeamRunResult(
            teamName,
            summary,
            succeeded.Sum(outcome => outcome.TurnsCompleted),
            succeeded.Any(outcome => outcome.MaxTurnsReached),
            Error: error,
            HadFailures: hadFailures);
    }

    private async Task<(bool Found, TeamConfig? Config)> TryResolveTeamAsync(
        string teamName,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        if (_teams.TryGetValue(teamName, out var existing))
            return (true, existing);

        var teamFile = TeamConfigLoader.GetTeamFilePath(teamName);
        if (teamFile is null)
        {
            var problem = AgentProblemDetails.ToolExecutionFailed(
                $"Team '{teamName}' not found. Use /team to list available teams, or place a team.yaml file under ~/.onecode/teams/{teamName}/.", toolName: "TeamOrchestration");
            eventSink?.Invoke(new OrchestrationEvent.Error(problem.Detail, problem));
            return (false, null);
        }

        await RegisterTeamAsync(teamName, teamFile, ct).ConfigureAwait(false);
        if (_teams.TryGetValue(teamName, out var loaded))
            return (true, loaded);

        var problem2 = AgentProblemDetails.ToolExecutionFailed(
            $"Team '{teamName}' could not be loaded.", toolName: "TeamOrchestration");
        eventSink?.Invoke(new OrchestrationEvent.Error(problem2.Detail, problem2));
        return (false, null);
    }

    private static TeamRunResult TeamError(
        string teamName, string detail, Action<OrchestrationEvent>? eventSink = null)
    {
        var problem = AgentProblemDetails.ToolExecutionFailed(detail, toolName: "TeamOrchestration");
        eventSink?.Invoke(new OrchestrationEvent.Error(problem.Detail, problem));
        return new TeamRunResult(teamName, problem.Detail, 0, false, Error: problem);
    }
}
