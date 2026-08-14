using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Encapsulates Build Run gate logic: controlled build attempts, terminal-reason
/// resolution, cancelled-run persistence, and plan-run completion.
///
/// Extracted from ChatService to isolate the Build/Plan workflow concern from
/// the streaming event loop. ChatService delegates to this class instead of
/// holding 9 build/plan-related dependencies directly.
/// </summary>
public sealed class BuildRunGate
{
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly IBuildRunCoordinator? _buildRunCoordinator;
    private readonly IClarificationInteractionService? _clarificationInteraction;
    private readonly ControlledBuildAttemptHost? _controlledBuildAttemptHost;
    private readonly IBuildRunStore? _buildRunStore;
    private readonly OneCode.Core.Workflows.IOperationLedger? _operationLedger;
    private readonly IPlanWorkflowApplicationService? _planWorkflow;
    private readonly PlanCardPublisher? _planCardPublisher;
    private readonly ISessionManager _sessionManager;
    private readonly IToolProtocolValidator _toolProtocolValidator;
    private readonly ILogger<BuildRunGate> _logger;

    public BuildRunGate(
        IMainAgentRunner mainAgentRunner,
        ISessionManager sessionManager,
        IToolProtocolValidator toolProtocolValidator,
        ILogger<BuildRunGate> logger,
        IBuildRunCoordinator? buildRunCoordinator = null,
        IClarificationInteractionService? clarificationInteraction = null,
        ControlledBuildAttemptHost? controlledBuildAttemptHost = null,
        IBuildRunStore? buildRunStore = null,
        OneCode.Core.Workflows.IOperationLedger? operationLedger = null,
        IPlanWorkflowApplicationService? planWorkflow = null,
        PlanCardPublisher? planCardPublisher = null)
    {
        _mainAgentRunner = mainAgentRunner;
        _sessionManager = sessionManager;
        _toolProtocolValidator = toolProtocolValidator;
        _logger = logger;
        _buildRunCoordinator = buildRunCoordinator;
        _clarificationInteraction = clarificationInteraction;
        _controlledBuildAttemptHost = controlledBuildAttemptHost;
        _buildRunStore = buildRunStore;
        _operationLedger = operationLedger;
        _planWorkflow = planWorkflow;
        _planCardPublisher = planCardPublisher;
    }

    /// <summary>Whether all required Build Run dependencies are configured.</summary>
    public bool IsConfigured => _buildRunCoordinator is not null
        && _controlledBuildAttemptHost is not null
        && _buildRunStore is not null;

    /// <summary>Whether Plan workflow integration is available.</summary>
    public bool HasPlanWorkflow => _planWorkflow is not null;

    public IBuildRunCoordinator? Coordinator => _buildRunCoordinator;
    public IClarificationInteractionService? Clarification => _clarificationInteraction;

    public Task<MainAgentRunResult> RunControlledBuildAttemptAsync(
        BuildRun buildRun,
        MainAgentRunOptions options,
        IReadOnlyList<AIFunction> localTools,
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
                static () => new EditTransaction(),
                static run => BuildRunStateEvent.From(run),
                Ledger: _operationLedger));
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
                options.ModelId,
                options.SystemPrompt,
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

    public static BuildTerminalReason ResolveTerminalReason(BuildRun run)
        => run.State == BuildRunState.Clarifying
            ? BuildTerminalReason.ClarificationRequired
            : run.TerminalReason ?? run.State switch
            {
                BuildRunState.Completed => BuildTerminalReason.Completed,
                BuildRunState.Blocked => BuildTerminalReason.Blocked,
                BuildRunState.Cancelled => BuildTerminalReason.Cancelled,
                BuildRunState.LimitReached => BuildTerminalReason.TurnLimitReached,
                BuildRunState.BudgetExceeded => BuildTerminalReason.BudgetExceeded,
                _ => BuildTerminalReason.AgentException,
            };

    public static BuildRunResult CreateBuildRunResult(BuildRun run, string? summary) =>
        new(
            run.Id,
            run.State,
            run.TerminalReason ?? BuildTerminalReason.AgentException,
            summary,
            run.ChangedFiles,
            run.Plan?.Tasks ?? [],
            run.Validations,
            run.Scope?.AcceptanceCriteria ?? [],
            run.Plan?.Risks ?? [],
            run.DeliveryManifest,
            run.TransactionCommitted,
            run.TransactionRolledBack,
            run.Metrics);

    internal async Task PersistCancelledRunAsync(
        SessionId? conversationId,
        string agentRunId,
        WorkingMode workingMode,
        ToolBatchCollector collector,
        CancellationToken ct)
    {
        if (conversationId is not { } sessionId)
            return;

        if (collector.CompletedBatches.Count > 0)
        {
            await _sessionManager.AppendCompletedToolBatchesAsync(
                sessionId,
                collector.CompletedBatches,
                ct).ConfigureAwait(false);
        }

        if (_planWorkflow is null || workingMode != WorkingMode.Plan)
            return;
        var workflow = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State is PlanWorkflowState.Completed or PlanWorkflowState.Failed or PlanWorkflowState.Cancelled)
        {
            return;
        }
        if (workflow.ActiveRunId is not null
            && !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        await _planWorkflow.CancelAsync(new CancelPlanCommand(
            $"cancel-run-{agentRunId}",
            sessionId,
            workflow.Id,
            workflow.Version,
            "Plan agent run was cancelled."), ct).ConfigureAwait(false);
    }

    public async Task CompletePlanRunIfPendingAsync(
        SessionId? conversationId,
        string agentRunId,
        WorkingMode workingMode,
        bool hasOpenToolBatch,
        CancellationToken ct)
    {
        if (_planWorkflow is null
            || conversationId is not { } sessionId
            || workingMode != WorkingMode.Plan)
        {
            return;
        }

        var workflow = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State != PlanWorkflowState.FinalizingPlanRun
            || !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        var protocolValid = !hasOpenToolBatch
            && _toolProtocolValidator.Validate(_sessionManager.GetChatHistory(sessionId)).IsValid;
        await _planWorkflow.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                workflow.Id,
                agentRunId,
                protocolValid,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (protocolValid && _planCardPublisher is not null)
        {
            var awaiting = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (awaiting?.State == PlanWorkflowState.AwaitingApproval
                && awaiting.SubmittedRevision is { } revision)
            {
                _planCardPublisher.Publish(awaiting);
            }
        }
    }

    public IBuildRunStore? BuildRunStore => _buildRunStore;
}
