using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;
using OneCode.App.Services.Runtime;
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
/// Agent 构建见 <see cref="TeamAgentFactory"/>，工作流运行见 <see cref="TeamWorkflowRunner"/>。
/// </summary>
public sealed partial class TeamOrchestrationService
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

    private readonly TeamClarificationWorkflowHost _clarificationWorkflowHost;
    private readonly RequestPortGate _approvalGate;
    private readonly ITeamRunStore _teamRunStore;
    private readonly OneCode.Core.Workflows.IOperationLedger? _operationLedger;
    private readonly TeamRegistry _registry;

    internal TeamOrchestrationService(
        TeamWorkflowRunner workflowRunner,
        ILoggerFactory loggerFactory,
        ILogger<TeamOrchestrationService> logger,
        TeamRunApplicationService teamRunService,
        TeamRequirementService requirementService,
        IClarificationInteractionService clarificationInteraction,
        IWorkingDirectoryAccessor workingDirectoryAccessor,
        TeamTaskWorkflowHost taskWorkflowHost,

        TeamClarificationWorkflowHost clarificationWorkflowHost,
        RequestPortGate approvalGate,
        ITeamRunStore teamRunStore,
        TeamRegistry registry,
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

        _clarificationWorkflowHost = clarificationWorkflowHost;
        _approvalGate = approvalGate;
        _teamRunStore = teamRunStore;
        _operationLedger = operationLedger;
        // 职责收敛：注册表（注册/发现/活跃团队）与结果聚合分别由 TeamRegistry / TeamResultAggregator 承担，
        // 本服务退化为用例门面（运行/恢复/审批/澄清编排）。
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IReadOnlyList<string> RegisteredTeams => _registry.RegisteredTeams;

    /// <summary>当前活跃团队。为 null 时回退到第一个注册的团队。</summary>
    public string? ActiveTeam
    {
        get => _registry.ActiveTeam;
        set => _registry.ActiveTeam = value;
    }

    /// <summary>获取当前应使用的团队名（ActiveTeam 或第一个注册的团队）</summary>
    public string? ResolveActiveTeam() => _registry.ResolveActiveTeam();

    public TeamOrchestrationMode? GetTeamMode(string teamName) =>
        _registry.TryGet(teamName, out var config) ? config.Mode : null;

    /// <summary>
    /// 返回指定团队的成员信息列表（AgentId + Role + 是否为 Orchestrator）。
    /// 用于 TUI 启动横幅显示成员构成，让用户知道这个团队有哪些角色。
    /// </summary>
    public IReadOnlyList<TeamMemberInfo>? GetTeamMembers(string teamName) =>
        _registry.GetMemberInfos(teamName);

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
            // 统一 YAML 格式，不再支持 JSON。注册表负责解析、advisory 透出与日志。
            _registry.RegisterFromFile(teamFilePath, teamName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register team '{TeamName}'", teamName);
        }
    }

    public Task UnregisterTeamAsync(string teamName, CancellationToken ct = default)
    {
        if (_registry.Remove(teamName))
            _logger.LogInformation("Team '{TeamName}' unregistered", teamName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 注册内置团队模板（从嵌入式资源加载）+ 扫描用户团队目录。
    /// 幂等：已注册的同名团队不会被覆盖。
    /// </summary>
    public Task RegisterBuiltinTeamsAsync(CancellationToken ct = default) =>
        _registry.RegisterBuiltinAndUserTeamsAsync(ct);

    /// <summary>
    /// 流式运行 Team — 通过 <paramref name="eventSink"/> 回调实时推送 OrchestrationEvent 给 TUI 层。
    /// eventSink 为 null 时仅返回最终输出。
    /// </summary>
    public async Task<TeamRunResult> RunTeamStreamingAsync(
        string teamName,
        string goal,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct = default,
        IReadOnlyList<string>? imagePaths = null,
        SessionId? sessionId = null)
    {
        var (found, config) = await TryResolveTeamAsync(teamName, eventSink, ct).ConfigureAwait(false);
        if (!found || config is null)
        {
            return TeamRunErrors.Fail(teamName, $"Team '{teamName}' not found.", eventSink);
        }

        // 编排模式由团队 YAML 的 template 字段固定声明，运行期不可覆盖。
        _logger.LogInformation(
            "Team '{TeamName}' streaming starting: mode={Mode} goal={Goal}",
            teamName, config.Mode, goal[..Math.Min(80, goal.Length)]);

        var orchestrator = config.Members.FirstOrDefault(m =>
            m.Role is "lead" or "orchestrator") ?? config.Members[0];
        eventSink?.Invoke(new OrchestrationEvent.AgentCoordination(
            CoreConstants.MessageTypes.User, null, orchestrator.AgentId, null, goal));

        var cwd = _workingDirectoryAccessor.WorkingDirectory;
        _logger.LogDebug("Team '{TeamName}' using working directory {WorkingDirectory}", teamName, cwd);
        var (fileChanges, observedSink) = TeamFileChangeObserver.CreateObservedSink(eventSink);
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
            return TeamRunErrors.Fail(teamName, $"需求澄清生成失败：{ex.Message}", eventSink);
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
                return TeamRunErrors.Fail(
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
                return TeamRunErrors.Fail(teamName, $"需求澄清生成失败：{ex.Message}", eventSink);
            }
            if (!analysis.CanProceedWithoutClarification)
            {
                return TeamRunErrors.Fail(
                    teamName,
                    "Team request is still ambiguous after clarification; no workflow or write transaction was started.",
                    eventSink);
            }
        }

        var plan = _requirementService.CreateImplementationPlan(analysis, config);

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
            return TeamRunErrors.Fail(
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
            return TeamRunErrors.Fail(sessionId,
                $"No resumable TeamRun exists for session '{sessionId}'.",
                eventSink);
        }

        var (found, config) = await TryResolveTeamAsync(teamRun.TeamName, eventSink, ct).ConfigureAwait(false);
        if (!found || config is null)
        {
            return TeamRunErrors.Fail(teamRun.TeamName, $"Team '{teamRun.TeamName}' not found.", eventSink);
        }

        // 编排模式由团队 YAML 固定声明，恢复直接使用当前注册的配置——
        // 不存在历史 override 需要还原（EffectiveMode 持久化已随运行期覆盖一起移除）。

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
                    return TeamRunErrors.Fail(teamRun.TeamName, "Team clarification was cancelled.", eventSink);
                var clarifiedGoal = $"{teamRun.OriginalRequest}\nClarification response:\n{clarification.Answer}";
                RequirementAnalysisResult analysis;
                try
                {
                    analysis = await _requirementService.AnalyzeAsync(clarifiedGoal, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return TeamRunErrors.Fail(teamRun.TeamName, $"需求澄清生成失败：{ex.Message}", eventSink);
                }
                if (!analysis.CanProceedWithoutClarification)
                    return TeamRunErrors.Fail(teamRun.TeamName, "Team request remains ambiguous.", eventSink);
                var clarifiedPlan = _requirementService.CreateImplementationPlan(analysis, config);
                teamRun = await _teamRunService.PromoteClarificationToApprovalAsync(
                    teamRun.Id, clarifiedGoal, clarifiedPlan, ct).ConfigureAwait(false);
            }

            var plan = teamRun.Plan
                ?? throw new InvalidOperationException($"TeamRun '{teamRun.Id}' has no approval plan.");
            var approval = await RunApprovalGateAsync(
                teamRun.TeamName, teamRun.Id, config, modelId, plan, eventSink, ct).ConfigureAwait(false);
            if (approval.ApprovalGranted != true)
                return TeamRunErrors.Fail(teamRun.TeamName, "Team plan was not approved.", eventSink);

            teamRun = await _teamRunService.BeginApprovedExecutionAsync(
                teamRun.Id, teamRun.TeamName, teamRun.OriginalRequest,
                teamRun.WorkingDirectory, plan, ct, teamRun.SessionId).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Resuming team session {Session} as a new execution generation (run {RunId})",
            sessionId, teamRun.Id);

        // C2: 恢复前先回滚上一世代 ledger 未提交的文件副作用，再校验 Succeeded 任务的
        // 改动是否仍在盘；不一致则降级重跑。顺序关键：若先比对指纹，崩溃后文件仍在盘
        // 会误判一致，随后 reconcile 又回滚 → 已完成工作静默丢失。BindAsync 内的
        // reconcile 对已回滚的 ledger 是幂等 no-op。
        if (_operationLedger is not null)
        {
            await _operationLedger.ReconcileRunAsync($"team/{teamRun.Id}", ct).ConfigureAwait(false);
        }
        teamRun = await _teamRunService.ReconcileSucceededTasksAsync(teamRun, ct).ConfigureAwait(false);

        var (fileChanges, observedSink) = TeamFileChangeObserver.CreateObservedSink(eventSink);

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
        try
        {
            var workflowResult = await _taskWorkflowHost.RunNextAsync(
                teamRun, config, modelId, runtime,
                new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);
            var result = TeamResultAggregator.Build(teamRun.TeamName, workflowResult.Outcomes);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // H1: 用户取消必须落库 Cancelled 终态（CancellationToken.None），
            // 否则聚合永远停在 Running，恢复列表把已取消的 run 当"进行中"。
            await CancelRunAsync(teamRun.Id, runtime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 取消落库：先 reconcile ledger（回滚未提交副作用，与内存事务回滚一致），
    /// 再持久化 Cancelled 终态；lease 已取得时同步关闭 workflow 记录。
    /// 任何落库失败仅记日志，不掩盖取消本身。
    /// </summary>
    private async Task CancelRunAsync(
        TeamRunId runId,
        TeamTaskWorkflowRuntime runtime,
        CancellationToken ct)
    {
        try
        {
            if (_operationLedger is not null)
                await _operationLedger.ReconcileRunAsync($"team/{runId}", ct).ConfigureAwait(false);
            await _teamRunService.CancelAsync(runId, "Team execution was cancelled by the user.", ct).ConfigureAwait(false);
            if (runtime.IsBound)
            {
                await _taskWorkflowHost.CompleteBusinessAsync(
                    runId, runtime.FencingToken,
                    OneCode.Core.Workflows.WorkflowRunState.Cancelled,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist cancellation for TeamRun {RunId}", runId);
        }
    }

    private async Task<(bool Found, TeamConfig? Config)> TryResolveTeamAsync(
        string teamName,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        if (_registry.TryGet(teamName, out var existing))
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
        if (_registry.TryGet(teamName, out var loaded))
            return (true, loaded);

        var problem2 = AgentProblemDetails.ToolExecutionFailed(
            $"Team '{teamName}' could not be loaded.", toolName: "TeamOrchestration");
        eventSink?.Invoke(new OrchestrationEvent.Error(problem2.Detail, problem2));
        return (false, null);
    }

}
