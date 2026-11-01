using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Observability;
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
    HistorySeedBoundary? seedBoundary = null,
    bool stripPlanExclusiveTools = false,
    ContextTokenTallyObserver? tally = null)
    : ChatHistoryProvider(
        provideOutputMessageFilter: ExcludeContextProviderMessages,
        storeInputRequestMessageFilter: ExcludeEphemeralRequestMessages)
{
    internal const string StateKey = "onecode.transcriptHistory";
    private const string AttributionKey = "_attribution";
    private const string SubmitPlanToolName = "SubmitPlan";

    /// <summary>run 生命周期的会话历史：转录播种 + MAF 每次服务调用回写的增量。</summary>
    public sealed class HistoryState
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];

        /// <summary>
        /// 当前回合由 <see cref="AIContextProvider"/> 注入的瞬态上下文消息（如计划交接上下文、模式指令、待办清单）。
        /// 在每次模型调用时锚定于最后一条真实 <c>User</c> 消息紧邻之前：
        /// 1. 保证首轮请求末尾是真实 <c>User</c> 消息、工具循环期间（含 <c>ToolApprovalAgent</c> 审批重入）请求末尾是最新 <c>Tool</c> 结果，
        ///    消除 <c>CompactionMessageIndex.Update</c> 因末尾 <c>AIContextProvider</c> 消息恒等而触发的静默丢消息短路；
        /// 2. 保证同回合 Call 1..N 前缀严格不变，<c>CompactionMessageIndex</c> 全程走增量追加而不清空已有压缩组；
        /// 3. 瞬态消息标记为 <see cref="AgentRequestMessageSourceType.AIContextProvider"/>，回写时自动滤除、跨轮整体替换。
        /// </summary>
        [JsonIgnore]
        public List<ChatMessage> TurnContextMessages { get; set; } = [];

        /// <summary>
        /// <see cref="Messages"/> 中已出现的 assistant 工具调用 <c>CallId</c> 集合，随回写增量维护。
        /// 审批恢复去重（<see cref="IsDuplicateSynthesizedToolCallMessage"/>）每次模型调用都要判定，
        /// 重扫整段历史会随工具轮数退化为 O(历史 × 轮数)。序列化快照不带该集合，反序列化后按需重建。
        /// </summary>
        [JsonIgnore]
        public HashSet<string>? AssistantCallIds { get; set; }

        /// <summary>
        /// 已打 <c>ChatHistory</c> 来源标记的历史快照（<see cref="Messages"/> 的克隆）。
        /// 每次模型调用都要把整段历史交给下游，逐次克隆会让成本变成 O(历史 × 轮数)；
        /// 这里缓存已打标的克隆，只对新增尾部克隆一次。
        /// </summary>
        [JsonIgnore]
        public List<ChatMessage>? StampedHistory { get; set; }

        /// <summary>构建 <see cref="StampedHistory"/> 时已处理的 <see cref="Messages"/> 前缀长度；变更即需要增量补齐。</summary>
        [JsonIgnore]
        public int StampedSourceCount { get; set; } = -1;
    }

    private readonly ProviderSessionState<HistoryState> _sessionState = new(
        _ => new HistoryState(), StateKey);

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [StateKey];

    /// <inheritdoc />
    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = _sessionState.GetOrInitializeState(context.Session);
        if (state.Messages.Count == 0)
        {
            state.Messages = SeedFromTranscript(context);
            // 播种整体替换了历史：缓存派生视图必须作废，否则会拿到上一次运行的旧快照
            state.StampedHistory = null;
            state.StampedSourceCount = -1;
            state.AssistantCallIds = null;
        }

        // 一次模型调用的组装起点：消息级分桶换代（待办清单等逐轮重复注入，累进会翻倍）
        tally?.BeginModelCall();

        var incomingContextMessages = new List<ChatMessage>();
        var nonContextRequestMessages = new List<ChatMessage>();
        foreach (var message in context.RequestMessages)
        {
            var sourceType = message.GetAgentRequestMessageSourceType();
            if (sourceType == AgentRequestMessageSourceType.AIContextProvider)
            {
                // 分桶只能在此处做：下方重发路径会用本 provider 自己的类型名重打标
                // （见 WithAgentRequestMessageSource），原始贡献者 sourceId 即不可再考。
                // 先读后克隆：DetachAttribution 的克隆与原消息共享内部集合（实测 Clone 为浅别名），
                // Remove(_attribution) 会就地清掉原始标记——sourceId 读取必须发生在克隆之前。
                // 已知边界：System-role 的 provider 消息经 MAF 基类预处理会丢 sourceId → 落 "(unknown)" 桶。
                var contributorId = message.GetAgentRequestMessageSourceId();
                var contributorText = message.Text;
                incomingContextMessages.Add(DetachAttribution(message));
                if (!string.IsNullOrEmpty(contributorText))
                    tally?.AddMessageTokens(contributorId, contributorText);
                continue;
            }

            if (sourceType == AgentRequestMessageSourceType.ChatHistory
                || IsDuplicateSynthesizedToolCallMessage(state, message))
            {
                continue;
            }

            message.MessageId ??= Guid.NewGuid().ToString("N");
            nonContextRequestMessages.Add(message);
        }

        var hasNewExternalUserTurn = nonContextRequestMessages.Any(static m =>
            m.Role == ChatRole.User
            && m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        if (incomingContextMessages.Count > 0)
        {
            if (!hasNewExternalUserTurn && state.TurnContextMessages.Count == incomingContextMessages.Count)
            {
                for (var i = 0; i < incomingContextMessages.Count; i++)
                {
                    incomingContextMessages[i].MessageId = state.TurnContextMessages[i].MessageId;
                }
            }

            state.TurnContextMessages = incomingContextMessages;
        }
        else if (hasNewExternalUserTurn)
        {
            state.TurnContextMessages = [];
        }

        var sourceId = GetType().FullName!;
        var historyMessages = GetStampedHistory(state, sourceId);

        var conversationMessages = new List<ChatMessage>(state.Messages.Count + nonContextRequestMessages.Count);
        conversationMessages.AddRange(historyMessages);
        conversationMessages.AddRange(nonContextRequestMessages);

        if (state.TurnContextMessages.Count == 0)
        {
            return new ValueTask<IEnumerable<ChatMessage>>(conversationMessages);
        }

        var turnContextWithSource = state.TurnContextMessages
            .Select(m => DetachAttribution(m).WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, sourceId))
            .ToList();

        if (conversationMessages.Count == 0)
        {
            return new ValueTask<IEnumerable<ChatMessage>>(turnContextWithSource);
        }

        var lastUserIndex = conversationMessages.FindLastIndex(static m => m.Role == ChatRole.User);
        var insertIndex = lastUserIndex >= 0 ? lastUserIndex : 0;

        var combined = new List<ChatMessage>(conversationMessages.Count + turnContextWithSource.Count);
        for (var i = 0; i < conversationMessages.Count; i++)
        {
            if (i == insertIndex)
            {
                combined.AddRange(turnContextWithSource);
            }

            combined.Add(conversationMessages[i]);
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
            if (IsDuplicateSynthesizedToolCallMessage(state, requestMessage))
            {
                continue;
            }

            var stored = DetachAttribution(requestMessage);
            state.Messages.Add(stored);
            TrackAssistantCallIds(state, stored);
        }

        if (context.ResponseMessages is { } responses)
        {
            foreach (var responseMessage in responses)
            {
                var stored = DetachAttribution(responseMessage);
                state.Messages.Add(stored);
                TrackAssistantCallIds(state, stored);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 把消息里的工具调用 <c>CallId</c> 增量并入 <see cref="HistoryState.AssistantCallIds"/>，
    /// 使审批恢复去重判定为 O(1) 而非每次重扫整段历史。
    /// </summary>
    private static void TrackAssistantCallIds(HistoryState state, ChatMessage message)
    {
        if (state.AssistantCallIds is null)
        {
            // 首次需要时按现有历史整体重建（已含刚追加的这条），之后只增量补齐
            state.AssistantCallIds = BuildAssistantCallIds(state.Messages);
            return;
        }

        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent { CallId: { Length: > 0 } callId })
                state.AssistantCallIds.Add(callId);
        }
    }

    private static HashSet<string> BuildAssistantCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
                continue;

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { CallId: { Length: > 0 } callId })
                    callIds.Add(callId);
            }
        }

        return callIds;
    }

    /// <summary>
    /// 判定请求消息是否为 <c>ToolApprovalAgent</c> 审批恢复时由 <c>FunctionInvokingChatClient</c>
    /// 合成的重复 <c>Assistant(FunctionCallContent)</c> 消息。
    /// </summary>
    private static bool IsDuplicateSynthesizedToolCallMessage(HistoryState state, ChatMessage candidate)
    {
        var existingHistory = state.Messages;
        if (candidate.Role != ChatRole.Assistant || existingHistory.Count == 0)
            return false;

        List<FunctionCallContent>? candidateCalls = null;
        var assistantContentCount = 0;
        foreach (var content in candidate.Contents)
        {
            if (content is FunctionCallContent call)
            {
                candidateCalls ??= [];
                candidateCalls.Add(call);
            }

            // 只有「整条消息都由工具调用构成」才可能是 FICC 合成的重复消息
            assistantContentCount++;
        }

        if (candidateCalls is null || candidateCalls.Count != assistantContentCount)
            return false;

        var existingCallIds = state.AssistantCallIds;
        if (existingCallIds is null)
        {
            existingCallIds = BuildAssistantCallIds(existingHistory);
            state.AssistantCallIds = existingCallIds;
        }

        if (existingCallIds.Count == 0)
            return false;

        foreach (var call in candidateCalls)
        {
            if (string.IsNullOrEmpty(call.CallId) || !existingCallIds.Contains(call.CallId))
                return false;
        }

        return true;
    }

    /// <summary>从转录播种历史，排除本轮输入用户消息（RequestMessages 已携带，避免重复送达）。</summary>
    private List<ChatMessage> SeedFromTranscript(InvokingContext context)
    {
        var history = chatHistoryReader.GetChatHistory(conversationId);
        // 阶段边界与非 Plan 模式剥离：
        // 1. 非 Plan 模式或存在阶段边界时剥离 SubmitPlan 工具调用对（PlanExclusive 工具在 Build 不挂载，
        //    即使受控 Build 续跑前因 /compact 导致 epoch 漂移使 seedBoundary 置空，保留尾部中的 SubmitPlan 也会被剥离）；
        // 2. 跨阶段冗长错误结果压缩仅在有效 seedBoundary.MessageIndex 范围内生效。
        var boundaryLimit = seedBoundary is { } boundary
            ? Math.Clamp(boundary.MessageIndex, 0, history.Count)
            : 0;
        var stripLimit = stripPlanExclusiveTools ? history.Count : boundaryLimit;
        if (stripLimit > 0 || boundaryLimit > 0)
            history = StripPlanExclusiveToolCallsBeforeBoundary(history, stripLimit, boundaryLimit);

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
    /// 取「已打 <c>ChatHistory</c> 来源标记」的历史快照，只对自上次构建以来新增的消息做克隆与打标。
    /// 返回只读包装：底层列表是会话状态本身（缓存复用），调用方对其增删/重排会写坏后续
    /// 模型调用的历史顺序，因此以类型层禁改而非约定。持有旧包装的调用方会看到后续追加
    /// （同一底层列表），与「历史是活状态」语义一致。
    /// </summary>
    private static IReadOnlyList<ChatMessage> GetStampedHistory(HistoryState state, string sourceId)
    {
        var stamped = state.StampedHistory;
        if (stamped is null || state.StampedSourceCount > state.Messages.Count)
        {
            stamped = new List<ChatMessage>(state.Messages.Count);
            state.StampedSourceCount = 0;
        }

        for (var i = state.StampedSourceCount; i < state.Messages.Count; i++)
        {
            // 瞬态上下文消息不进请求历史的持久前缀（由 TurnContextMessages 单独锚定注入）。
            if (state.Messages[i].GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider)
                continue;

            stamped.Add(DetachAttribution(state.Messages[i])
                .WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory, sourceId));
        }

        state.StampedHistory = stamped;
        state.StampedSourceCount = state.Messages.Count;
        return stamped.AsReadOnly();
    }

    /// <summary>
    /// 剥离指定范围内的 <c>SubmitPlan</c> 工具调用与对应结果，并将阶段边界之前冗长的失败工具结果
    /// （<c>IsError == true</c>）收拢为单行错误摘要，保留其余全部探索对话与成功只读工具结果（100% 零信息损失）。
    /// 若某条消息在剥离后不再包含任何有效内容（如仅含 <c>SubmitPlan</c> 调用的 assistant 消息或仅含其结果的 tool 消息），
    /// 则整条消息一并移除，确保不会残留空消息或孤儿 tool 消息。
    /// </summary>
    private static List<ChatMessage> StripPlanExclusiveToolCallsBeforeBoundary(
        IReadOnlyList<ChatMessage> history,
        int stripLimit,
        int errorCompactLimit)
    {
        var maxLimit = Math.Clamp(Math.Max(stripLimit, errorCompactLimit), 0, history.Count);
        if (maxLimit == 0)
            return history.ToList();

        var clampedStripLimit = Math.Clamp(stripLimit, 0, history.Count);
        var clampedErrorLimit = Math.Clamp(errorCompactLimit, 0, history.Count);

        var strippedCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < clampedStripLimit; i++)
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
        for (var i = 0; i < maxLimit; i++)
        {
            var message = history[i];
            var canStrip = i < clampedStripLimit;
            var canCompactError = i < clampedErrorLimit;

            var hasStrippedContent = canStrip && message.Contents.Any(content =>
                (content is FunctionCallContent call
                    && string.Equals(call.Name, SubmitPlanToolName, StringComparison.Ordinal))
                || (content is FunctionResultContent result && strippedCallIds.Contains(result.CallId)));
            var hasCompactedErrorContent = canCompactError && message.Contents.Any(static content =>
                content is FunctionResultContent result && ShouldCompactPriorPhaseError(result));

            if (!hasStrippedContent && !hasCompactedErrorContent)
            {
                filtered.Add(message);
                continue;
            }

            var remaining = message.Contents
                .Where(content =>
                    !canStrip
                    || (!(content is FunctionCallContent call
                            && string.Equals(call.Name, SubmitPlanToolName, StringComparison.Ordinal))
                        && !(content is FunctionResultContent result && strippedCallIds.Contains(result.CallId))))
                .Select(content => canCompactError && content is FunctionResultContent result
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

        for (var i = maxLimit; i < history.Count; i++)
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
        message.MessageId ??= Guid.NewGuid().ToString("N");
        var clone = message.Clone();
        clone.MessageId = message.MessageId;
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
