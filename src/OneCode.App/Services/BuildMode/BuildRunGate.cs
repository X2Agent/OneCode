using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Encapsulates Build Run gate logic: controlled build attempts, terminal-reason
/// resolution, cancelled-run persistence, and plan-run completion.
/// </summary>
public sealed class BuildRunGate(
    IMainAgentRunner mainAgentRunner,
    ISessionManager sessionManager,
    ILogger<BuildRunGate> logger,
    IBuildRunCoordinator? buildRunCoordinator = null,
    ControlledBuildAttemptHost? controlledBuildAttemptHost = null,
    IBuildRunStore? buildRunStore = null,
    PlanRunCloser? planRunCloser = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly IMainAgentRunner _mainAgentRunner = mainAgentRunner;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly ILogger<BuildRunGate> _logger = logger;
    private readonly IBuildRunCoordinator? _buildRunCoordinator = buildRunCoordinator;
    private readonly ControlledBuildAttemptHost? _controlledBuildAttemptHost = controlledBuildAttemptHost;
    private readonly IBuildRunStore? _buildRunStore = buildRunStore;
    private readonly PlanRunCloser? _planRunCloser = planRunCloser;

    /// <summary>Whether all required Build Run dependencies are configured.</summary>
    public bool IsConfigured => _buildRunCoordinator is not null
        && _controlledBuildAttemptHost is not null
        && _buildRunStore is not null;

    /// <summary>Whether Plan workflow integration is available.</summary>
    public bool HasPlanWorkflow => _planRunCloser is not null;

    public IBuildRunCoordinator? Coordinator => _buildRunCoordinator;

    public Task<MainAgentRunResult> RunControlledBuildAttemptAsync(
        BuildRun buildRun,
        MainAgentRunOptions options,
        System.Threading.Channels.ChannelWriter<object> eventWriter,
        CancellationToken ct)
    {
        var host = _controlledBuildAttemptHost
            ?? throw new InvalidOperationException("Controlled Build attempt host is not configured.");
        var coordinator = _buildRunCoordinator
            ?? throw new InvalidOperationException("BuildRun coordinator is not configured.");
        var store = _buildRunStore
            ?? throw new InvalidOperationException("BuildRun store is not configured.");
        var runtime = new ControlledBuildAttemptRuntime(
            _mainAgentRunner,
            coordinator,
            store,
            new ControlledBuildAttemptContext(
                options,
                eventWriter,
                // 事务必须带 logger：受控 attempt 失败时 dispose 回滚会删除新建文件，
                // 无日志的回滚在事故中完全不可观测（文件消失却查无痕迹）。
                () => new EditTransaction(loggerFactory?.CreateLogger<EditTransaction>()),
                // durableStateObserver → BuildRunStateEvent 双通道改造——
                // attempt 运行时以 OrchestrationEvent.BuildStateProjectionChanged 统一信封发射，
                // StreamingSession.Digest 为其解信封唯一投影点。
                static run => new OrchestrationEvent.BuildStateProjectionChanged(run),
                loggerFactory?.CreateLogger<ControlledBuildAttemptRuntime>()));
        var toolCapabilityHash = ControlledBuildAttemptWorkflowCompiler.ComputeToolCapabilityHash(
            ControlledBuildAttemptWorkflowCompiler.ApprovedPolicyCapabilities(buildRun, options.ToolCapabilities),
            buildRun.ApprovedToolPolicy?.ToolNames ?? []);

        return RunControlledBuildAttemptCoreAsync(host, buildRun, options, toolCapabilityHash, runtime, eventWriter, ct);
    }

    private static async Task<MainAgentRunResult> RunControlledBuildAttemptCoreAsync(
        ControlledBuildAttemptHost host,
        BuildRun buildRun,
        MainAgentRunOptions options,
        string toolCapabilityHash,
        ControlledBuildAttemptRuntime runtime,
        System.Threading.Channels.ChannelWriter<object> eventWriter,
        CancellationToken ct)
    {
        try
        {
            var result = await host.RunNextAsync(
                buildRun,
                options.ModelId ?? string.Empty,
                options.SystemPrompt ?? string.Empty,
                toolCapabilityHash,
                runtime,
                new System.Text.Json.JsonSerializerOptions(),
                ct: ct).ConfigureAwait(false);
            return result.Output.Result;
        }
        finally
        {
            eventWriter.TryComplete();
        }
    }

    public static RunTerminalReason ResolveTerminalReason(BuildRun run)
        => run.TerminalReason ?? run.State switch
        {
            BuildRunState.Completed => RunTerminalReason.Completed,
            BuildRunState.Blocked => RunTerminalReason.Blocked,
            BuildRunState.Cancelled => RunTerminalReason.Cancelled,
            BuildRunState.LimitReached => RunTerminalReason.TurnLimitReached,
            BuildRunState.BudgetExceeded => RunTerminalReason.BudgetExceeded,
            _ => RunTerminalReason.AgentException,
        };

    public static BuildRunResult CreateBuildRunResult(BuildRun run, string? summary)
    {
        var effectiveSummary = !string.IsNullOrWhiteSpace(summary)
            ? summary
            : run.Plan?.Summary;
        return new(
            run.Id,
            run.State,
            run.TerminalReason ?? RunTerminalReason.AgentException,
            effectiveSummary,
            run.ChangedFiles,
            run.Plan?.Tasks ?? [],
            run.Validations,
            run.Scope?.AcceptanceCriteria ?? [],
            run.Plan?.Risks ?? [],
            run.DeliveryManifest,
            run.TransactionCommitted,
            run.TransactionRolledBack,
            run.Metrics);
    }

    /// <summary>
    /// Plan run 收口的唯一入口：run 以任意终态离场（正常结束、输出被拦截、异常、取消）都必须经过这里，
    /// 否则已提交的计划会停在 <see cref="PlanWorkflowState.FinalizingPlanRun"/>，审批入口永不出现。
    /// 对非 Plan 模式、无工作流的情形幂等 no-op。
    /// </summary>
    /// <remarks>
    /// <see cref="PlanRunEnd.Cancelled"/> 先落盘已封口的工具批次，再写工作流取消（落盘必须先于取消写，
    /// 否则取消后转录缺失工具结果）。
    /// </remarks>
    internal async Task CloseOutPlanRunAsync(
        SessionId? conversationId,
        string agentRunId,
        WorkingMode workingMode,
        PlanRunEnd end,
        ToolBatchCollector? collector,
        CancellationToken ct)
    {
        if (conversationId is not { } sessionId)
            return;

        if (end == PlanRunEnd.Cancelled && collector is { CompletedBatches.Count: > 0 })
        {
            await _sessionManager.AppendCompletedToolBatchesAsync(
                sessionId,
                collector.CompletedBatches,
                ct).ConfigureAwait(false);
        }

        if (_planRunCloser is null || workingMode != WorkingMode.Plan)
            return;

        if (end == PlanRunEnd.Cancelled)
            await _planRunCloser.CancelAsync(sessionId, agentRunId, ct).ConfigureAwait(false);
        else
            await _planRunCloser.FinishAsync(sessionId, agentRunId, collector?.HasOpenBatch ?? false, ct).ConfigureAwait(false);
    }

    public IBuildRunStore? BuildRunStore => _buildRunStore;
}
