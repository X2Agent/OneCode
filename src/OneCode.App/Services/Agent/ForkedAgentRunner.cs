using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Errors;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

public sealed class ForkedAgentRunner : IAgentRunner
{
    private readonly ILogger<ForkedAgentRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatClient _chatClient;
    // IServiceProvider 仅用于传递给 MAF 的 ChatClientAgentBuildOptions.ServiceProvider
    // （MAF 框架要求），不得用于业务逻辑中的 GetService<T>() 调用。
    private readonly IServiceProvider _serviceProvider;
    private readonly SharedContextProviderBuilder _sharedContextBuilder;
    private readonly SubAgentPipelineFactory _pipelineFactory;
    private readonly IModelManager _modelManager;
    private readonly IWorkingDirectoryAccessor _workingDirectoryAccessor;
    private readonly Core.Tools.ToolMetadataRegistry _toolMetadata;
    private readonly CompactionProviderBuilder _compactionBuilder;
    private readonly PromptComposer _promptComposer;
    private readonly ConcurrentDictionary<string, ForkedAgentRun> _activeRuns = new();

    public ForkedAgentRunner(
        ILogger<ForkedAgentRunner> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        SharedContextProviderBuilder sharedContextBuilder,
        SubAgentPipelineFactory pipelineFactory,
        ForkedAgentRuntimeDependencies runtime,
        PromptComposer promptComposer)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _sharedContextBuilder = sharedContextBuilder;
        _pipelineFactory = pipelineFactory;
        _chatClient = runtime.ChatClient;
        _modelManager = runtime.ModelManager;
        _workingDirectoryAccessor = runtime.WorkingDirectory;
        _toolMetadata = runtime.ToolMetadata;
        _compactionBuilder = runtime.CompactionBuilder;
        _promptComposer = promptComposer;
    }

    public async Task<ForkedAgentResult> RunForkedAgentAsync(
        ForkedAgentParams parameters,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new ForkedAgentRun
        {
            Id = runId,
            Label = parameters.ForkLabel ?? "unnamed",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = cts,
        };

        _activeRuns[runId] = run;

        try
        {
            _logger.LogDebug("Starting forked agent '{Label}' (id={RunId})", run.Label, runId);

            using var transaction = new EditTransaction(
                _loggerFactory.CreateLogger<EditTransaction>());

            var csp = parameters.CacheSafeParams;
            var maxTurns = parameters.MaxTurns ?? 10;
            var tools = csp?.Tools?
                .Where(tool => parameters.Capabilities?.AllowedToolNames.Contains(tool.Name) != false)
                .ToList() ?? (List<AITool>)[];
            var cwd = _workingDirectoryAccessor.WorkingDirectory;
            var profile = parameters.Profile;

            var contextProviders = _sharedContextBuilder.BuildCommon(
                SharedContextProviderBuilder.ApplyProfileDefaults(
                    profile,
                    new AgentContextProviderOptions
                    {
                        WorkingDirectory = cwd,
                        ChatClient = _chatClient,
                        CodeActTools = tools,
                        ConversationId = parameters.ConversationId,
                    }));

            _logger.LogInformation(
                "Forked agent '{Label}' (profile={Profile}) armed with {Count} tools, cwd={Cwd}",
                parameters.ForkLabel, profile, tools.Count, cwd);

            var providerId = csp?.ModelId is null ? null : _modelManager.Resolve(csp.ModelId)?.ProviderId;
            var linkedToken = cts.Token;

            var pipelineOptions = _pipelineFactory.BuildOptions(new SubAgentPipelineRequest
            {
                Profile = profile,
                WorkingDirectory = cwd,
                EditTransaction = transaction,
                MaxToolCalls = maxTurns,
                ModelId = csp?.ModelId,
                ProviderId = providerId,
                ConversationId = parameters.ConversationId,
                AllowedTools = parameters.AllowedTools,
            });

            var pipeline = AgentPipelineBuilder.BuildChatClientAgent(new ChatClientAgentBuildOptions
            {
                ChatClient = _chatClient,
                Name = parameters.ForkLabel ?? "sub-agent",
                ChatOptions = new ChatOptions
                {
                    ModelId = csp?.ModelId,
                    MaxOutputTokens = parameters.MaxOutputTokens ?? 4096,
                    Tools = tools.Count > 0 ? tools : null,
                    ToolMode = tools.Count > 0 ? ChatToolMode.Auto : null,
                },
                LoggerFactory = _loggerFactory,
                ServiceProvider = _serviceProvider,
                ChatClientContextProviders =
                [
                    await _compactionBuilder.BuildForWorkerAsync(
                        csp?.ModelId,
                        parameters.MaxOutputTokens,
                        linkedToken).ConfigureAwait(false)
                ],
                AgentContextProviders = contextProviders,
                ToolMetadata = _toolMetadata,
                PipelineOptions = pipelineOptions,
            });

            List<ChatMessage> chatMessages = [];

            if (csp?.SystemPrompt is { Length: > 0 } sysPrompt)
                chatMessages.Add(new ChatMessage(ChatRole.System, sysPrompt));

            if (parameters.ForkContextMessages != null)
                chatMessages.AddRange(parameters.ForkContextMessages);

            if (parameters.PromptMessages != null)
                chatMessages.AddRange(parameters.PromptMessages);

            var session = await pipeline.Agent.CreateSessionAsync(linkedToken).ConfigureAwait(false);
            var response = await pipeline.Agent.RunAsync(chatMessages, session, new AgentRunOptions(), linkedToken)
                .ConfigureAwait(false);

            var result = new ForkedAgentResult
            {
                Text = response.Text,
                Messages = response.Messages.ToList(),
                TotalInputTokens = (long)(response.Usage?.InputTokenCount ?? 0),
                TotalOutputTokens = (long)(response.Usage?.OutputTokenCount ?? 0),
                TurnCount = 1 + pipeline.Metrics.ToolCallCount,
            };

            _logger.LogDebug(
                "Forked agent '{Label}' completed: {ToolCalls} tool calls, {Input}+{Output} tokens",
                run.Label, pipeline.Metrics.ToolCallCount, result.TotalInputTokens, result.TotalOutputTokens);

            transaction.Commit();
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Forked agent '{Label}' was cancelled", run.Label);
            return new ForkedAgentResult { Messages = Array.Empty<ChatMessage>() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forked agent '{Label}' failed", run.Label);
            var problemDetails = AgentProblemDetails.ToolExecutionFailed(
                detail: ex.Message,
                toolName: parameters.ForkLabel);
            return new ForkedAgentResult
            {
                Messages = Array.Empty<ChatMessage>(),
                Error = problemDetails,
            };
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            cts.Dispose();
        }
    }

    public int ActiveRunCount => _activeRuns.Count;

    public void CancelAll()
    {
        foreach (var run in _activeRuns.Values)
        {
            try { run.CancellationTokenSource?.Cancel(); } catch { /* already cancelled */ }
        }
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var profile = PipelineProfileBehavior.FromAgentType(request.Agent);
        var roleInstruction = PipelineProfileBehavior.GetRoleInstruction(profile);

        IReadOnlyList<ChatMessage>? forkContext = null;
        if (roleInstruction is not null)
        {
            var system = await _promptComposer.ComposeWithRoleAsync(roleInstruction, ct).ConfigureAwait(false);
            forkContext = [new ChatMessage(ChatRole.System, system)];
        }

        var childAllowedTools = profile is PipelineProfile.Explore or PipelineProfile.Plan
            ? PipelineProfileBehavior.ReadOnlyAgentTools
            : request.CacheSafeParams?.Tools?.Select(tool => tool.Name).ToList();
        var requestedCapabilities = request.CacheSafeParams?.ToolCapabilities;
        var capabilities = request.ParentCapabilities is not null && requestedCapabilities is not null
            ? request.ParentCapabilities.Intersect(requestedCapabilities)
            : request.ParentCapabilities ?? requestedCapabilities;

        var parameters = new ForkedAgentParams
        {
            PromptMessages = [new ChatMessage(ChatRole.User, request.Prompt)],
            ForkContextMessages = forkContext,
            ForkLabel = request.Agent,
            Profile = profile,
            MaxTurns = request.MaxTurns ?? 50,
            CacheSafeParams = request.CacheSafeParams,
            AllowedTools = profile is PipelineProfile.Explore or PipelineProfile.Plan
                ? PipelineProfileBehavior.ReadOnlyAgentTools
                : null,
            Capabilities = capabilities,
        };

        var result = await RunForkedAgentAsync(parameters, ct).ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogWarning(
                "Forked agent '{Agent}' returned problem details: {Type} - {Detail} (traceId={TraceId})",
                request.Agent, result.Error.Type, result.Error.Detail, result.Error.TraceId ?? "(none)");
        }

        return new AgentRunResult(
            Agent: request.Agent,
            ConversationId: SessionId.NewId(),
            Output: result.Text ?? "",
            TurnsCompleted: result.TurnCount,
            MaxTurnsReached: result.TurnCount >= (parameters.MaxTurns ?? 50),
            Error: result.Error);
    }

    private sealed class ForkedAgentRun
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
    }
}
