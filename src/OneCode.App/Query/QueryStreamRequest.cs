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
    /// <summary>
    /// 本轮附加输入消息（如 input hook 的 AdditionalContexts），位于 <see cref="UserPrompt"/> 之前。
    /// 多轮历史不在此字段——由 <c>TranscriptChatHistoryProvider</c> 经 MAF 契约提供。
    /// </summary>
    IReadOnlyList<ChatMessage>? RunInputMessages,
    bool IncludeNextPrompt,
    IReadOnlyList<AIFunction> LocalTools,
    string AgentRunId,
    bool ControlledExecution,
    WorkingMode WorkingMode,
    Action<FileChange>? FileChangeCallback,
    BuildPlan? PrescribedBuildPlan = null,
    string? HarnessInstructions = null);
