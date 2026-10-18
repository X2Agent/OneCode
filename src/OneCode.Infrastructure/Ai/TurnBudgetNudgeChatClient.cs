using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：轮次预算计数 + 收尾提醒。每次模型调用计数（Harness 迭代 = 一次模型往返）并写入
/// <see cref="AgentPipelineMetrics.ModelCallCount"/>，达到轮数上限的 <see cref="NudgeRatio"/>（80%）时
/// 向请求末尾注入一条收尾提醒，让模型带着预算意识收尾，而不是闷头跑到被轮数上限硬停。
///
/// <para>
/// 失控研究循环的根因是模型对自身轮次预算零可见性（Claude Code 社区对同款问题的
/// 诉求修复即 ~80% 收尾提醒）。注入消息随单次请求送达，不进入会话历史——
/// 提醒一次通常足以让模型开始总结；确实不理会时由轮数上限（Harness 迭代上限）兜底。
/// </para>
///
/// <para>
/// 装饰器实例随每次子代理 run 新建（计数从 0 起）；只安装在子代理管道上——
/// 主会话管道长生命周期跨多条用户消息，逐调用计数会失真，且主会话已有
/// token 版 BudgetGuard 分级预警。
/// </para>
/// </summary>
public sealed class TurnBudgetNudgeChatClient(IChatClient inner, int maxTurns, AgentPipelineMetrics metrics)
    : DelegatingChatClient(inner)
{
    /// <summary>提醒触发比例：达到轮数上限的 80% 即提醒（业界对失控循环的主修法）。</summary>
    internal const double NudgeRatio = 0.8;

    /// <summary>小任务不提醒：阈值 ≤ 4 轮时提醒本身消耗预算，得不偿失。</summary>
    internal const int MinTurnsForNudge = 5;

    /// <summary>
    /// 收尾提醒注入指令：预算过半有余即收，验证只留关键缺口，不展开新的广域检索。
    /// 与 BudgetGuard 的收尾注入同一措辞风格（动词前置、禁止项明确）。
    /// </summary>
    internal const string NudgeInstruction =
        "[Turn budget] You have used about 80% of your turn budget for this task. " +
        "Do NOT start new broad research or searches. " +
        "Verify only critical remaining gaps, then produce your final summary of findings now.";

    private readonly int _nudgeAt = maxTurns > MinTurnsForNudge
        ? (int)Math.Ceiling(maxTurns * NudgeRatio)
        : int.MaxValue;
    private bool _nudged;

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

    /// <summary>
    /// 计数并在达到阈值时于请求末尾追加提醒（仅一次）。克隆消息列表——注入的消息
    /// 只属于当次请求，不得回写调用方序列（MAF 会话历史不含该消息，下轮自然消失）。
    /// </summary>
    private IList<ChatMessage> PrepareMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var modelCalls = metrics.IncrementModelCallCount();
        if (_nudged || modelCalls != _nudgeAt)
            return list;

        _nudged = true;
        var nudged = new List<ChatMessage>(list) { new(ChatRole.User, NudgeInstruction) };
        return nudged;
    }
}
