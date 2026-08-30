using Microsoft.Extensions.AI;
using OneCode.Core.Build;

namespace OneCode.App.Query;

/// <summary>
/// Parameter object for one streaming run through <see cref="QueryStreamEngine.StreamCoreAsync"/>
/// — converges the former 17-parameter core signature into named, self-documenting fields.
/// </summary>
internal sealed record QueryStreamRequest(
    string SystemPrompt,
    string ModelId,
    int? ThinkingBudget,
    SessionId? SessionId,
    string? WorkingDirectory,
    SessionId? ConversationId,
    string UserPrompt,
    bool IsMultimodal,
    ChatMessage? LastUserMessage,
    IReadOnlyList<ChatMessage>? HistoryMessages,
    bool IncludeNextPrompt,
    IReadOnlyList<AIFunction> LocalTools,
    string AgentRunId,
    bool ControlledExecution,
    WorkingMode WorkingMode,
    Action<FileChange>? FileChangeCallback,
    BuildPlan? PrescribedBuildPlan = null);
