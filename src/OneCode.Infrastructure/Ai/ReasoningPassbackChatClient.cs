using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// DeepSeek thinking 模式兼容：将历史 assistant 消息中的推理文本
/// （Microsoft.Extensions.AI 的 <see cref="TextReasoningContent"/>）经 AsyncLocal
/// 侧信道传递给 <see cref="OpenAiReasoningPassbackHandler"/>，由后者在 HTTP 请求体中
/// 为每条 assistant 消息注入 <c>reasoning_content</c> 字段。
/// </summary>
/// <remarks>
/// 背景：DeepSeek V3.2+ / V4 thinking 模式（带工具调用的场景）要求——中间 assistant
/// 消息的 <c>reasoning_content</c> 必须在后续所有请求中原样回传，否则返回
/// HTTP 400 "The reasoning_content in the thinking mode must be passed back to the API."。
/// OpenAI .NET SDK 2.x 的 Chat Completions 请求模型没有该字段，
/// M.E.AI 的 OpenAIChatClient 在消息转换时会丢弃 <see cref="TextReasoningContent"/>，
/// 因此必须在 HTTP 层补写。
/// </remarks>
public sealed class ReasoningPassbackChatClient(IChatClient inner) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 物化一次：快照供侧信道与 inner 共用，避免对 lazy 序列的双重枚举。
        var snapshot = messages.ToList();
        ReasoningPassbackContext.Scope = ReasoningPassbackContext.Capture(snapshot);
        try
        {
            return await inner.GetResponseAsync(snapshot, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReasoningPassbackContext.Scope = null;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        ReasoningPassbackContext.Scope = ReasoningPassbackContext.Capture(snapshot);
        try
        {
            await foreach (var update in inner.GetStreamingResponseAsync(snapshot, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            ReasoningPassbackContext.Scope = null;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>AsyncLocal 侧信道：同一异步流内从装饰器向 HTTP handler 传递快照。</summary>
internal static class ReasoningPassbackContext
{
    private static readonly AsyncLocal<ReasoningPassbackScope?> Current = new();

    public static ReasoningPassbackScope? Scope
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    /// <summary>
    /// 按消息顺序提取每条 assistant 消息的推理文本（无推理的消息用空串占位，
    /// 保持与请求体中 assistant 消息的顺序对齐）。
    /// </summary>
    public static ReasoningPassbackScope Capture(IReadOnlyList<ChatMessage> messages)
    {
        List<string> reasonings = new(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
                continue;

            var text = string.Concat(
                message.Contents.OfType<TextReasoningContent>().Select(static c => c.Text));
            reasonings.Add(text ?? string.Empty);
        }

        return new ReasoningPassbackScope(reasonings);
    }
}

internal sealed record ReasoningPassbackScope(IReadOnlyList<string> AssistantReasonings)
{
    public bool HasReasoning => AssistantReasonings.Any(static s => s.Length > 0);
}

/// <summary>
/// 回传开关判定：DeepSeek thinking 模式需要 reasoning_content 回传；
/// 其余 provider 的请求模型没有该字段，注入反而可能被严格校验拒绝。
/// </summary>
public static class ReasoningPassbackPolicy
{
    public static bool ShouldEnable(string? providerId, string? baseUrl, string? model) =>
        ContainsDeepSeek(providerId) || ContainsDeepSeek(baseUrl) || ContainsDeepSeek(model);

    private static bool ContainsDeepSeek(string? value) =>
        value?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) == true;
}
