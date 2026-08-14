using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Tui;
using Microsoft.Extensions.AI;

namespace OneCode.App.Query;

/// <summary>
/// Chat service — thin facade preserving the public query contract
/// (<see cref="IConversationRunner"/> + <see cref="ICacheSafeParamsProvider"/>) for TUI and
/// BackgroundService (Cron/AutoDream) consumers. All streaming orchestration — Build 门禁、事件消化、
/// 终结记账 — is delegated to <see cref="QueryStreamEngine"/> (ADR 0006).
/// </summary>
/// <remarks>
/// 实现 <see cref="IConversationRunner"/> 的原因：headless 触发器（CronJobExecutor 等）依赖接口而非
/// 本具体类，以断开 ChatService → ToolCatalog → CronCreateTool → CronSchedulerService →
/// ICronJobExecutor → ChatService 的循环依赖。引擎在构造函数内组装（组合根模式，与 BuildRunGate 一致），
/// 不单独注册 DI，避免扩大组合面。
/// </remarks>
public sealed class ChatService : IConversationRunner, ICacheSafeParamsProvider
{
    private readonly ILogger<ChatService> _logger;
    private readonly QueryStreamEngine _engine;

    /// <inheritdoc />
    public CacheSafeParams? Current => _engine.LastCacheSafeParams;

    /// <summary>Alias for <see cref="Current"/> kept for call sites that still read the snapshot by name.</summary>
    public CacheSafeParams? LastCacheSafeParams => _engine.LastCacheSafeParams;

    public ChatService(
        ILogger<ChatService> logger,
        IMainAgentRunner mainAgentRunner,
        IToolCatalog toolCatalog,
        IHookExecutionService hookExecutionService,
        ChatSessionDependencies session,
        ChatObservabilityDependencies observability)
    {
        _logger = logger;
        _engine = new QueryStreamEngine(
            logger,
            mainAgentRunner,
            toolCatalog,
            hookExecutionService,
            session,
            observability);
    }

    /// <summary>Convenience overload.</summary>
    public IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        string prompt,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        IReadOnlyList<string>? imagePaths = null)
    {
        var userMessage = QueryStreamEngine.BuildUserMessage(prompt, imagePaths, _logger);
        var messages = new List<ChatMessage> { userMessage };
        return StreamQueryAsync(messages, systemPrompt, modelId, thinkingBudget, null, null, ct, workingMode, fileChangeCallback);
    }

    /// <summary>
    /// Streaming — delegated to the engine, which drives the MAF ChatClientAgent via
    /// MainAgentRunner.RunStreamingAsync and preserves the IAsyncEnumerable&lt;QueryEvent&gt; contract
    /// (cancellation flows through the engine's [EnumeratorCancellation] parameter).
    /// </summary>
    public IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        IList<ChatMessage> messages,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        SessionId? sessionId = null,
        string? workingDirectory = null,
        CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null)
        => _engine.StreamInteractiveAsync(messages, systemPrompt, modelId, thinkingBudget, sessionId, workingDirectory, ct, workingMode, fileChangeCallback);

    /// <summary>
    /// Starts an application-orchestrated run with an explicit context boundary.
    /// It does not append a synthetic user message and does not replay the planning transcript.
    /// </summary>
    public IAsyncEnumerable<QueryEvent> StreamWorkflowRunAsync(
        WorkflowRunRequest request,
        CancellationToken ct = default)
        => _engine.StreamWorkflowAsync(request, ct);
}
