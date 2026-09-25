using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Session;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 把 MAF <see cref="ChatHistoryProvider"/> 契约桥接到 Session 事件溯源转录。
///
/// 读：取转录历史并排除本轮已落库的用户消息（<c>QueryStreamEngine</c> 在 run 前写入该消息），
/// 否则会与 RequestMessages 重复送达模型。
/// 写：不实现（基类默认空操作）——转录写入含 historyEpoch/fencing 语义，仍由宿主负责。
/// </summary>
internal sealed class TranscriptChatHistoryProvider(
    ISessionChatHistoryReader chatHistoryReader,
    SessionId conversationId) : ChatHistoryProvider
{
    /// <inheritdoc />
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var history = chatHistoryReader.GetChatHistory(conversationId);
        if (history.Count == 0)
            return new ValueTask<IEnumerable<ChatMessage>>([]);

        var latestUserText = context.RequestMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?.Text;

        var result = string.IsNullOrEmpty(latestUserText)
            ? history
            : AgentEventDigester.BuildHistoryWithoutLatestUser(history, latestUserText);

        return new ValueTask<IEnumerable<ChatMessage>>(result);
    }
}
