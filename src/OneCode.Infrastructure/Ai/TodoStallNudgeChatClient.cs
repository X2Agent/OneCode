using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：计划执行期的「待办停滞」提醒。距上一次待办变更（add/complete/remove）已累计达到
/// <see cref="StallThreshold"/> 次工具调用仍未更新待办时，在当次请求末尾持续附带一条瞬态提醒，催模型完成当前步骤即刻勾选。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：步骤进度的唯一事实源是 agent 自己的待办清单，而模型完全可能跨多个步骤连续调用工具、
/// 到收尾时才一次性批量调用 <c>todos_complete</c>——导致界面进度长时间卡在第一步随后瞬间跳过中间步骤。
/// </para>
/// <para>
/// 为什么是 IChatClient 装饰器：上下文 provider 每个 run 只注入一次，而停滞发生在 run 内部的
/// 工具循环里，只有模型调用边界能逐请求观察「自上次勾选以来的调用数」。装饰器放在工具调用循环之下，
/// 每次模型往返都会经过它。提醒只属于当次请求（不写回会话历史），因此在超阈值期间持续保留也不会在历史中堆积重复消息；
/// 一旦模型调用 <c>todos_complete</c>，计数归零，提醒立即消失。
/// </para>
/// </remarks>
public sealed class TodoStallNudgeChatClient(IChatClient inner, int stallThreshold = TodoStallNudgeChatClient.StallThreshold)
    : DelegatingChatClient(inner)
{
    /// <summary>自上次待办变更起累计这么多次工具调用即持续提醒：4 次调用足以完成单步的并行检索与落盘。</summary>
    internal const int StallThreshold = 4;

    internal const string NudgeInstruction =
        "[Todo progress] You have made several tool calls since you last updated your todo list. " +
        "If the current step's goal is met, call todos_complete with its numeric id immediately before moving on " +
        "(todos_get_remaining lists the ids; you may call todos_complete in parallel with the next step's first tool call). " +
        "Never batch multiple steps' todos_complete calls at the end of the run, and do NOT repeat searches or fetches " +
        "whose results are already in this conversation.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(PrepareMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(PrepareMessages(messages), options, cancellationToken);

    private IList<ChatMessage> PrepareMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (CountCallsSinceLastTodoMutation(list) < stallThreshold)
            return list;

        // 克隆：注入消息只属于当次请求，不得回写调用方序列。
        return new List<ChatMessage>(list) { new(ChatRole.User, NudgeInstruction) };
    }

    /// <summary>自最近一次待办变更调用起（不含）的工具调用数；历史里没有变更调用时为全部调用数。</summary>
    private static int CountCallsSinceLastTodoMutation(IList<ChatMessage> messages)
    {
        var count = 0;
        for (var m = messages.Count - 1; m >= 0; m--)
        {
            var contents = messages[m].Contents;
            for (var c = contents.Count - 1; c >= 0; c--)
            {
                if (contents[c] is not FunctionCallContent call)
                    continue;

                if (HarnessProviderTools.TodoMutationToolNames.Contains(call.Name))
                    return count;

                count++;
            }
        }

        return count;
    }
}
