using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：当收到 stop_reason=max_tokens（即 ChatFinishReason.Length）时，
/// 先一步升级到 EscalatedMaxTokens (65536)，然后通过 multi-turn continuation prompt 恢复，
/// 最多 RecoveryLimit=3 次。
///
/// 实际恢复策略（对应 ChatService.cs TryHandleNoToolCallsAsync 第 674-706 行）：
///   Step 1: 默认值 → 65536（一步升级）
///   Step 2: 注入 continuation prompt + 重试（最多3次）
///
/// 注意：此机制依赖 M.E.AI 标准 ChatFinishReason.Length，与具体提供商无关。
/// 此装饰器在 MainAgentRunner 构建时与 CompactionProvider 一起组装，不在 DI 单例层注册。
/// </summary>
public sealed class MaxOutputTokensDecorator : IChatClient
{
    private readonly IChatClient _inner;
    private const int RecoveryLimit = 3;
    private const int EscalatedMaxTokens = 65_536;
    private const string RecoveryPrompt =
        "Output token limit hit. Resume directly — no apology, no recap. " +
        "Continue from exactly where you left off.";

    public MaxOutputTokensDecorator(IChatClient inner)
    {
        _inner = inner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var escalated = false;
        var recoveryCount = 0;

        // Capture the caller's original options reference and MaxOutputTokens
        // so we can restore them in a finally block — mutating the caller's
        // ChatOptions would leak escalation state into subsequent calls.
        var callerOptions = options;
        var originalMaxOutputTokens = options?.MaxOutputTokens;

        try
        {
            while (true)
            {
                var result = await _inner.GetResponseAsync(messageList, options, cancellationToken);

                if (result.FinishReason != ChatFinishReason.Length)
                    return result;

                if (!escalated)
                {
                    options ??= new ChatOptions();
                    options.MaxOutputTokens = EscalatedMaxTokens;
                    escalated = true;
                    continue;
                }

                if (recoveryCount >= RecoveryLimit)
                    return result;

                var partialText = result.Text;
                if (!string.IsNullOrWhiteSpace(partialText))
                    messageList.Add(new ChatMessage(ChatRole.Assistant, partialText));
                messageList.Add(new ChatMessage(ChatRole.User, RecoveryPrompt));
                options!.MaxOutputTokens = null;
                recoveryCount++;
            }
        }
        finally
        {
            // Restore caller's original MaxOutputTokens to avoid mutation side effects.
            // Only restores on the caller's original object (not on locally-created options).
            if (callerOptions is not null)
                callerOptions.MaxOutputTokens = originalMaxOutputTokens;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 流式路径不处理 Length 恢复：恢复逻辑需要完整响应才能判断 FinishReason，
        // 而流式响应在消费完毕后才暴露 FinishReason，无法在中间注入 continuation。
        // 非流式路径 (GetResponseAsync) 已覆盖完整的升级+continuation 恢复策略。
        // 流式调用方如需恢复，应在流结束后自行检查 FinishReason 并重新发起请求。
        return _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose() => (_inner as IDisposable)?.Dispose();
}
