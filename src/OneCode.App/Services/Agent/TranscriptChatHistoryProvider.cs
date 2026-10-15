using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Session;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 把 MAF <see cref="ChatHistoryProvider"/> 契约桥接到 Session 事件溯源转录。
///
/// Harness 管线以 per-service-call 持久化运行（<c>HarnessAgent</c> 硬编码
/// <c>RequirePerServiceCallChatHistoryPersistence=true</c>）：FunctionInvokingChatClient 收到
/// 装饰器的哨兵 ConversationId 后在工具调用迭代间清空自身缓冲，assistant(tool_calls) 与
/// 工具结果只经 InvokedAsync 回写到达 provider——provider 必须实现回写侧，否则第二轮模型
/// 请求只剩孤儿 tool 消息，严格上游确定性拒绝（HTTP 200 + 空 SSE，2026-10-01 OpenRouter
/// stealth 模型实测根因，参见 <c>docs/maf/integration-guide.md</c>）。
///
/// 读：会话状态为空时从转录播种（排除本轮已落库的输入用户消息——run 启动时
/// <c>QueryStreamEngine</c> 已写入转录、RequestMessages 又携带该消息；识别输入用户消息
/// 只看非中间件注入的 External 消息，避免 Harness 注入的 todo 等上下文消息干扰匹配）；
/// 状态非空（续轮恢复的快照）时直接使用状态——它是 run 内唯一的新鲜历史来源，工具批次
/// 在回合结束后才落转录，中途重读转录反而拿不到在途消息。
/// 写：InvokedAsync 送达的请求/响应消息追加进会话状态；请求侧过滤掉本 provider 盖戳供应
/// 的历史（基类默认行为）与 AIContextProvider 注入的上下文消息（todo 等，逐轮重复注入，
/// 不入账避免累积）。基类契约保证失败路径（用户中断、流异常）不调用回写。
/// 状态经 <see cref="ProviderSessionState{TState}"/> 存于 session StateBag，随 mafSession
/// 快照序列化跨轮恢复，受 <c>MafSessionInvalidator</c> 的 historyEpoch 围栏保护：
/// compact/clear/snip 等结构性变更丢弃快照，下一次运行从转录重新播种。
/// </summary>
internal sealed class TranscriptChatHistoryProvider(
    ISessionChatHistoryReader chatHistoryReader,
    SessionId conversationId,
    HistorySeedBoundary? seedBoundary = null)
    : ChatHistoryProvider(
        provideOutputMessageFilter: ExcludeContextProviderMessages,
        storeInputRequestMessageFilter: ExcludeEphemeralRequestMessages)
{
    private const string StateKey = "onecode.transcriptHistory";
    private const string AttributionKey = "_attribution";
    private const string SubmitPlanToolName = "SubmitPlan";

    /// <summary>run 生命周期的会话历史：转录播种 + MAF 每次服务调用回写的增量。</summary>
    public sealed class HistoryState
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];

        /// <summary>
        /// 当前回合首轮请求由 <see cref="AIContextProvider"/> 注入的瞬态上下文消息（如计划交接上下文、模式指令、待办清单）。
        /// <c>ChatClientAgent</c> 仅在每次 <c>RunAsync</c> 入口调用一次 <c>AIContextProviders</c>，而
        /// <c>FunctionInvokingChatClient</c> 在收到 per-service-call 哨兵后会清空自身请求缓冲；
        /// 此处缓存当轮上下文消息并在同轮后续工具迭代（Call 2..N）中锚定在最新 User 消息之后重放，
        /// 既保证工具循环期间计划/模式指令不丢失且前缀严格追加，又在新一轮对话开始时整体替换、不入持久化历史。
        /// </summary>
        [JsonIgnore]
        public List<ChatMessage> TurnContextMessages { get; set; } = [];
    }

    private readonly ProviderSessionState<HistoryState> _sessionState = new(
        _ => new HistoryState(), StateKey);

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [StateKey];

    /// <inheritdoc />
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (state.Messages.Count == 0)
        {
            state.Messages = SeedFromTranscript(context);
        }

        var incomingContextMessages = context.RequestMessages
            .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider)
            .Select(DetachAttribution)
            .ToList();

        if (incomingContextMessages.Count > 0)
        {
            state.TurnContextMessages = incomingContextMessages;
            return new ValueTask<IEnumerable<ChatMessage>>(
                state.Messages.Select(DetachAttribution).ToList());
        }

        if (context.RequestMessages.Any(m =>
                m.Role == ChatRole.User
                && m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External))
        {
            state.TurnContextMessages = [];
        }

        if (state.TurnContextMessages.Count == 0)
        {
            return new ValueTask<IEnumerable<ChatMessage>>(
                state.Messages.Select(DetachAttribution).ToList());
        }

        var combined = new List<ChatMessage>(state.Messages.Count + state.TurnContextMessages.Count);
        var lastUserIndex = state.Messages.FindLastIndex(m => m.Role == ChatRole.User);
        for (var i = 0; i < state.Messages.Count; i++)
        {
            combined.Add(DetachAttribution(state.Messages[i]));
            if (i == lastUserIndex)
            {
                combined.AddRange(state.TurnContextMessages.Select(DetachAttribution));
            }
        }

        if (lastUserIndex < 0)
        {
            combined.InsertRange(0, state.TurnContextMessages.Select(DetachAttribution));
        }

        return new ValueTask<IEnumerable<ChatMessage>>(combined);
    }

    /// <inheritdoc />
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        foreach (var requestMessage in context.RequestMessages)
        {
            state.Messages.Add(DetachAttribution(requestMessage));
        }

        if (context.ResponseMessages is { } responses)
        {
            foreach (var responseMessage in responses)
            {
                state.Messages.Add(DetachAttribution(responseMessage));
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>从转录播种历史，排除本轮输入用户消息（RequestMessages 已携带，避免重复送达）。</summary>
    private List<ChatMessage> SeedFromTranscript(InvokingContext context)
    {
        var history = chatHistoryReader.GetChatHistory(conversationId);
        // 阶段边界：approved-plan 受控 Build run 保留 Plan 阶段的对话与只读检索结果
        // （Read / Grep / WebSearch / WebFetch 等直接复用并命中 Prompt Cache，避免 Build 从零重搜），
        // 仅剥离边界之前的 SubmitPlan 工具调用对（PlanExclusive 工具在 Build 不挂载，
        // 成对剥离后无孤儿 tool 消息且不触发历史工具名回放）。
        if (seedBoundary is { } boundary)
            history = StripPlanExclusiveToolCallsBeforeBoundary(history, boundary.MessageIndex);

        var latestUserText = context.RequestMessages
            .LastOrDefault(message =>
                message.Role == ChatRole.User
                && message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider)?.Text;

        var seeded = string.IsNullOrEmpty(latestUserText)
            ? history
            : AgentEventDigester.BuildHistoryWithoutLatestUser(history, latestUserText);
        return seeded.Select(DetachAttribution).ToList();
    }

    private const int MaxPriorPhaseErrorResultChars = 240;

    /// <summary>
    /// 剥离阶段边界之前的 <c>SubmitPlan</c> 工具调用与对应结果，并将边界之前冗长的失败工具结果
    /// （<c>IsError == true</c>）收拢为单行错误摘要，保留其余全部探索对话与成功只读工具结果（100% 零信息损失）。
    /// 若某条消息在剥离后不再包含任何有效内容（如仅含 <c>SubmitPlan</c> 调用的 assistant 消息或仅含其结果的 tool 消息），
    /// 则整条消息一并移除，确保不会残留空消息或孤儿 tool 消息。
    /// </summary>
    private static List<ChatMessage> StripPlanExclusiveToolCallsBeforeBoundary(
        IReadOnlyList<ChatMessage> history,
        int boundaryIndex)
    {
        var limit = Math.Clamp(boundaryIndex, 0, history.Count);
        if (limit == 0)
            return history.ToList();

        var strippedCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < limit; i++)
        {
            foreach (var content in history[i].Contents)
            {
                if (content is FunctionCallContent { CallId: { Length: > 0 } callId } call
                    && string.Equals(call.Name, SubmitPlanToolName, StringComparison.Ordinal))
                {
                    strippedCallIds.Add(callId);
                }
            }
        }

        var filtered = new List<ChatMessage>(history.Count);
        for (var i = 0; i < limit; i++)
        {
            var message = history[i];
            var hasStrippedContent = message.Contents.Any(content =>
                (content is FunctionCallContent call
                    && string.Equals(call.Name, SubmitPlanToolName, StringComparison.Ordinal))
                || (content is FunctionResultContent result && strippedCallIds.Contains(result.CallId)));
            var hasCompactedErrorContent = message.Contents.Any(static content =>
                content is FunctionResultContent result && ShouldCompactPriorPhaseError(result));

            if (!hasStrippedContent && !hasCompactedErrorContent)
            {
                filtered.Add(message);
                continue;
            }

            var remaining = message.Contents
                .Where(content =>
                    !(content is FunctionCallContent call
                        && string.Equals(call.Name, SubmitPlanToolName, StringComparison.Ordinal))
                    && !(content is FunctionResultContent result && strippedCallIds.Contains(result.CallId)))
                .Select(static content => content is FunctionResultContent result
                    ? CompactPriorPhaseErrorIfNeeded(result)
                    : content)
                .ToList();

            var hasMeaningfulContent = remaining.Any(static content =>
                content is not TextContent textContent || !string.IsNullOrWhiteSpace(textContent.Text));
            if (!hasMeaningfulContent)
                continue;

            filtered.Add(new ChatMessage(message.Role, remaining)
            {
                AuthorName = message.AuthorName,
                AdditionalProperties = message.AdditionalProperties is { } props ? new(props) : null,
            });
        }

        for (var i = limit; i < history.Count; i++)
            filtered.Add(history[i]);

        return filtered;
    }

    private static bool ShouldCompactPriorPhaseError(FunctionResultContent result)
    {
        if (result.Result is not string text || text.Length <= MaxPriorPhaseErrorResultChars)
            return false;

        OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(text, out var isError, out _);
        return isError;
    }

    private static AIContent CompactPriorPhaseErrorIfNeeded(FunctionResultContent result)
    {
        if (result.Result is not string text || text.Length <= MaxPriorPhaseErrorResultChars)
            return result;

        OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(text, out var isError, out var unwrapped);
        if (!isError)
            return result;

        var firstLine = unwrapped
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? unwrapped;
        if (firstLine.Length > MaxPriorPhaseErrorResultChars)
            firstLine = firstLine[..MaxPriorPhaseErrorResultChars] + "...";
        if (!firstLine.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
            firstLine = $"[ERROR] {firstLine}";

        return new FunctionResultContent(result.CallId, $"{firstLine} [prior-phase error compacted]")
        {
            Exception = result.Exception,
        };
    }

    /// <summary>
    /// 输出过滤：仅排除 AIContextProvider 注入的瞬态消息（兜底净化历史快照中可能残留的上下文注入），
    /// 绝不排除 ChatHistory 或 External 消息。
    /// </summary>
    private static IEnumerable<ChatMessage> ExcludeContextProviderMessages(IEnumerable<ChatMessage> messages)
        => messages.Where(message =>
            message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider);

    /// <summary>
    /// 回写入账过滤（挂在 <c>storeInputRequestMessageFilter</c>）：排除已由历史/压缩器供应的消息
    /// （<see cref="AgentRequestMessageSourceType.ChatHistory"/>）与 <see cref="AIContextProvider"/>
    /// 注入的瞬态上下文消息（todo / 模式指令 / 计划交接上下文等逐轮重复注入，不入账避免累积）。
    /// </summary>
    private static IEnumerable<ChatMessage> ExcludeEphemeralRequestMessages(IEnumerable<ChatMessage> messages)
        => messages.Where(message =>
        {
            var sourceType = message.GetAgentRequestMessageSourceType();
            return sourceType != AgentRequestMessageSourceType.ChatHistory
                && sourceType != AgentRequestMessageSourceType.AIContextProvider;
        });

    /// <summary>
    /// 克隆消息并剥离请求期 <c>_attribution</c> 标记与流式残留的空 <see cref="TextContent"/> 块：
    /// 隔离 <c>state.Messages</c> 与下游 <c>CompactionMessageIndex</c> 持有的消息实例，防止跨轮原地改写污染持久化历史；
    /// 同时剔除 <c>Text</c> 为空串或 null 的 <see cref="TextContent"/>，防止推理流逐 chunk 产生的空文本块膨胀请求体。
    /// </summary>
    private static ChatMessage DetachAttribution(ChatMessage message)
    {
        var clone = message.Clone();
        clone.AdditionalProperties?.Remove(AttributionKey);
        for (var i = clone.Contents.Count - 1; i >= 0; i--)
        {
            if (clone.Contents[i] is TextContent { Text: null or "" })
            {
                clone.Contents.RemoveAt(i);
            }
        }

        return clone;
    }
}
