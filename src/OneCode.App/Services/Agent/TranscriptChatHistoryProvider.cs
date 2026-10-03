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
    SessionId conversationId) : ChatHistoryProvider(ExcludeEphemeralRequestMessages)
{
    private const string StateKey = "onecode.transcriptHistory";

    /// <summary>run 生命周期的会话历史：转录播种 + MAF 每次服务调用回写的增量。</summary>
    public sealed class HistoryState
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
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

        return new ValueTask<IEnumerable<ChatMessage>>(state.Messages);
    }

    /// <inheritdoc />
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        state.Messages.AddRange(context.RequestMessages);
        if (context.ResponseMessages is { } responses)
        {
            state.Messages.AddRange(responses);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>从转录播种历史，排除本轮输入用户消息（RequestMessages 已携带，避免重复送达）。</summary>
    private List<ChatMessage> SeedFromTranscript(InvokingContext context)
    {
        var history = chatHistoryReader.GetChatHistory(conversationId);
        var latestUserText = context.RequestMessages
            .LastOrDefault(message =>
                message.Role == ChatRole.User
                && message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider)?.Text;

        return string.IsNullOrEmpty(latestUserText)
            ? [.. history]
            : [.. AgentEventDigester.BuildHistoryWithoutLatestUser(history, latestUserText)];
    }

    /// <summary>
    /// 回写入账过滤：排除本 provider 盖戳供应的历史（防重播种）与 AIContextProvider
    /// 注入的上下文消息（todo 等逐轮重复注入，属运行时上下文而非对话历史）。
    /// </summary>
    private static IEnumerable<ChatMessage> ExcludeEphemeralRequestMessages(IEnumerable<ChatMessage> messages)
        => messages.Where(message =>
        {
            var sourceType = message.GetAgentRequestMessageSourceType();
            return sourceType != AgentRequestMessageSourceType.ChatHistory
                && sourceType != AgentRequestMessageSourceType.AIContextProvider;
        });
}
