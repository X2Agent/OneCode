using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Context;

namespace OneCode.App.Services;

/// <summary>
/// 模式感知的 AttachmentProvider 基类。
/// 提供 Turn 1 跳过 + full/sparse 交替 + 缓存的公共逻辑。
/// 子类只需覆写 IsInMode、LoadFullInstructionsAsync、GetSparseReminder。
///
/// Turn count is tracked per-conversation via <see cref="ToolActivationContext.CurrentConversationId"/>
/// to prevent cross-conversation contamination when this provider is singleton-scoped.
/// </summary>
public abstract class ModeAwareAttachmentProviderBase : ReadOnlyAIContextProviderBase
{
    /// <summary>
    /// 每 N 轮交替 full/sparse 提醒的间隔。
    /// </summary>
    protected const int FullSparseAlternationInterval = 5;

    private readonly ConcurrentDictionary<string, int> _turnCounts = new();
    private string? _cachedFullInstructions;

    /// <summary>当前是否处于该模式。</summary>
    protected abstract bool IsInMode { get; }

    /// <summary>加载完整模式指令（5/10/15... 轮次注入）。</summary>
    protected abstract Task<string> LoadFullInstructionsAsync(CancellationToken ct);

    /// <summary>获取 sparse reminder 文案（2/3/4/6/7... 轮次注入）。</summary>
    protected abstract string GetSparseReminder(int turnCount);

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        if (!IsInMode) return new AIContext();

        var convId = ToolActivationContext.CurrentConversationId ?? "default";
        var currentTurn = _turnCounts.AddOrUpdate(convId, 1, (_, count) => count + 1);

        // Turn 1 的完整指令由 MAF AgentModeProvider.Instructions 负责，
        // 此处跳过避免重复注入。
        if (currentTurn == 1) return new AIContext();

        var isFullTurn = currentTurn % FullSparseAlternationInterval == 0;
        if (isFullTurn)
        {
            // Full reminder: 重新加载完整工作流指令（首次加载后缓存）
            _cachedFullInstructions ??= await LoadFullInstructionsAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(_cachedFullInstructions))
            {
                return new AIContext
                {
                    Messages = [new ChatMessage(ChatRole.System, _cachedFullInstructions)],
                };
            }
        }
        else
        {
            // Sparse reminder: 简短提醒，避免重复注入完整工作流文本
            var reminder = GetSparseReminder(currentTurn);
            if (!string.IsNullOrEmpty(reminder))
            {
                return new AIContext
                {
                    Messages = [new ChatMessage(ChatRole.System, reminder)],
                };
            }
        }

        return new AIContext();
    }

    /// <summary>重置指定会话的轮次计数（新会话时调用，确保下次从 Turn 1 开始）</summary>
    public void ResetTurnCount(string? conversationId = null)
    {
        if (conversationId is { } id)
            _turnCounts.TryRemove(id, out _);
        else
            _turnCounts.Clear();
        _cachedFullInstructions = null;
    }
}
