// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Session;
using OneCode.Core.Models;
using System.Text;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Bidirectional provider for session-scoped factual memory.
/// <see cref="ProvideAIContextAsync"/> injects remembered facts before each LLM call;
/// <see cref="StoreAIContextAsync"/> extracts durable facts from the completed exchange
/// (LLM-summarised with heuristic fallback) under throttling, so it doesn't run every turn.
/// Throttling counters live in <see cref="ProviderSessionState{T}"/> and persist with the session.
/// </summary>
public sealed class SessionMemoryContextProvider : AIContextProvider
{
    private const int MinTokensBeforeExtraction = 2000;
    private const int MinTurnsBetweenExtractions = 5;
    private const int MinMessagesForExtraction = 4;
    private const int MaxInjectedMemories = 5;

    private readonly ISessionMemoryService _sessionMemoryService;
    private readonly ISessionConversationAccess _sessionManager;
    private readonly IChatClient _chatClient;
    private readonly ILogger<SessionMemoryContextProvider> _logger;
    private readonly ProviderSessionState<SessionMemoryState> _sessionState;
    private readonly IModelManager _modelManager;
    private readonly SessionId? _conversationId;

    public SessionMemoryContextProvider(
        ISessionMemoryService sessionMemoryService,
        ILogger<SessionMemoryContextProvider> logger,
        ISessionConversationAccess sessionManager,
        IChatClient chatClient,
        IModelManager modelManager,
        SessionId? conversationId = null)
        : base(provideInputMessageFilter: null,
               storeInputRequestMessageFilter: null,
               storeInputResponseMessageFilter: null)
    {
        ArgumentNullException.ThrowIfNull(sessionMemoryService);
        ArgumentNullException.ThrowIfNull(logger);
        _sessionMemoryService = sessionMemoryService;
        _logger = logger;
        _sessionManager = sessionManager;
        _chatClient = chatClient;
        _modelManager = modelManager;
        _conversationId = conversationId;
        _sessionState = new ProviderSessionState<SessionMemoryState>(
            stateInitializer: _ => new SessionMemoryState(),
            stateKey: nameof(SessionMemoryContextProvider));
    }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var conversation = _conversationId is { } id
            ? _sessionManager.GetConversation(id)
            : _sessionManager.ForegroundConversation;
        if (conversation is null)
            return new(new AIContext());

        var memories = _sessionMemoryService.GetMemories(conversation);
        if (memories.Count == 0)
            return new(new AIContext());

        var top = memories
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.UpdatedAt)
            .Take(MaxInjectedMemories)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## Session memories");
        sb.AppendLine("Relevant facts/preferences recorded earlier in this session:");
        foreach (var m in top)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {m.Content}");

        return new(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, sb.ToString())],
        });
    }

    protected override async ValueTask StoreAIContextAsync(
        AIContextProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
            return;

        var conversation = _conversationId is { } id
            ? _sessionManager.GetConversation(id)
            : _sessionManager.ForegroundConversation;
        if (conversation is null)
            return;

        var state = _sessionState.GetOrInitializeState(context.Session);
        if (!ShouldExtract(conversation, state))
            return;

        IReadOnlyList<string> extracted;
        try
        {
            extracted = await ExtractAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session memory extraction failed, falling back to heuristic.");
            extracted = _sessionMemoryService.ExtractKeyFacts(conversation);
        }

        await _sessionMemoryService
            .MergeExtractedMemoriesAsync(conversation, extracted, source: "auto", cancellationToken)
            .ConfigureAwait(false);

        state.LastExtractedMessageCount = conversation.Messages.Count;
        state.LastExtractedTurnCount = conversation.Messages.Count / 2;
        state.TotalTokenEstimate = EstimateTotalTokens(conversation);
        _sessionState.SaveState(context.Session, state);
    }

    private static bool ShouldExtract(Conversation conversation, SessionMemoryState state)
    {
        if (conversation.Messages.Count < MinMessagesForExtraction)
            return false;

        if (state.LastExtractedMessageCount == 0)
            return true;

        if (conversation.Messages.Count < state.LastExtractedMessageCount + 2)
            return false;

        if (conversation.Messages.Count / 2 - state.LastExtractedTurnCount < MinTurnsBetweenExtractions)
            return false;

        if (EstimateTotalTokens(conversation) - state.TotalTokenEstimate < MinTokensBeforeExtraction)
            return false;

        return true;
    }

    private static int EstimateTotalTokens(Conversation conversation)
    {
        var totalLength = 0;
        foreach (var msg in conversation.Messages)
        {
            totalLength += msg switch
            {
                UserMessage um => um.Content?.Length ?? 0,
                AssistantMessage am => am.Content.Sum(c => c is TextBlock tb ? tb.Text.Length : 0),
                SystemMessage sm => sm.Content?.Length ?? 0,
                _ => 0
            };
        }
        return totalLength / 4;
    }

    private async Task<IReadOnlyList<string>> ExtractAsync(Conversation conversation, CancellationToken ct)
    {
        var transcript = BuildTranscript(conversation);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You extract concise reusable session memories for a coding assistant."),
            new(ChatRole.User,
                "Extract durable session memories from this transcript. Return only one memory per line, " +
                "no numbering, no markdown headings. Keep each memory under 140 characters and only include " +
                "facts/preferences/constraints worth reusing later.\n\n" + transcript),
        };

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions
            {
                ModelId = _modelManager.GetFastModel().Id,
                MaxOutputTokens = 384,
            }, ct)
            .ConfigureAwait(false);

        var lines = response.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', '•', ' '))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(6)
            .ToList();

        return lines.Count > 0 ? lines : _sessionMemoryService.ExtractKeyFacts(conversation);
    }

    private static string BuildTranscript(Conversation conversation)
    {
        var sb = new StringBuilder();
        foreach (var message in conversation.Messages.TakeLast(16))
        {
            switch (message)
            {
                case UserMessage user:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"User: {user.Content}");
                    break;
                case AssistantMessage assistant:
                    var text = string.Join(" ", assistant.Content.OfType<TextBlock>().Select(b => b.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(CultureInfo.InvariantCulture, $"Assistant: {text}");
                    break;
                case SystemMessage system:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"System: {system.Content}");
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    private sealed class SessionMemoryState
    {
        [System.Text.Json.Serialization.JsonPropertyName("lastExtractedMessageCount")]
        public int LastExtractedMessageCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lastExtractedTurnCount")]
        public int LastExtractedTurnCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalTokenEstimate")]
        public int TotalTokenEstimate { get; set; }
    }
}
