using Microsoft.Extensions.AI;
using OneCode.Core.Build;

namespace OneCode.App.Query;

/// <summary>
/// Parameter object for one streaming run through <see cref="QueryStreamEngine.StreamCoreAsync"/>
/// — converges the former 17-parameter core signature into named, self-documenting fields.
/// </summary>
/// <remarks>
/// <see cref="SystemPrompt"/> carries the agent body only. <see cref="HarnessInstructions"/> carries
/// the shared harness fragment separately, because MAF composes the two (<c>HarnessInstructions</c>
/// first, then the body); passing a pre-joined string would duplicate the fragment. Null (AutoDream
/// and other stand-alone paths) leaves MAF's generic default instructions in place.
/// </remarks>
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
    BuildPlan? PrescribedBuildPlan = null,
    string? HarnessInstructions = null);
