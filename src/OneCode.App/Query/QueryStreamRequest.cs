using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
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
    // 本轮附加输入消息（如 input hook 的 AdditionalContexts），位于 UserPrompt 之前。
    // 多轮历史不在此字段——由 TranscriptChatHistoryProvider 经 MAF 契约提供。
    IReadOnlyList<ChatMessage>? RunInputMessages,
    bool IncludeNextPrompt,
    IReadOnlyList<AIFunction> LocalTools,
    string AgentRunId,
    bool ControlledExecution,
    WorkingMode WorkingMode,
    Action<FileChange>? FileChangeCallback,
    BuildPlan? PrescribedBuildPlan = null,
    string? HarnessInstructions = null,
    // 本 run 的工具能力集。随请求显式传递：AsyncLocal ambient 不跨 async iterator 的
    // yield 边界存续，StreamCoreAsync 需在 run 启动前据此重建 ToolActivationContext。
    ToolCapabilitySet? ToolCapabilities = null,
    // 阶段播种边界（approved-plan 受控 run）：provider 只播种边界之后的转录。
    // null = 全量转录播种（交互路径）。
    HistorySeedBoundary? HistorySeedBoundary = null);
