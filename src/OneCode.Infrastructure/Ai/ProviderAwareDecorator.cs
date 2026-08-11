using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：根据构造时确定的提供商，
/// 自动注入对应的请求参数（cache_control、thinking、reasoning_effort 等）。
/// Provider 在 DI 注册时就已确定（Anthropic / OpenAI / Ollama），运行期间不变。
/// </summary>
public sealed class ProviderAwareDecorator : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _providerId;
    private readonly int? _ollamaNumCtx;

    public ProviderAwareDecorator(IChatClient inner, string providerId, int? ollamaNumCtx = null)
    {
        _inner = inner;
        _providerId = providerId;
        _ollamaNumCtx = ollamaNumCtx;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        ApplyProviderStrategy(messageList, options);
        return await _inner.GetResponseAsync(messageList, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        ApplyProviderStrategy(messageList, options);
        return _inner.GetStreamingResponseAsync(messageList, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose() => (_inner as IDisposable)?.Dispose();

    private void ApplyProviderStrategy(IList<ChatMessage> messages, ChatOptions? options)
    {
        switch (_providerId)
        {
            case CoreConstants.ModelProviders.Anthropic:
                InjectAnthropicStrategy(messages, options);
                break;

            case CoreConstants.ModelProviders.OpenAI:
                InjectOpenAIStrategy(options);
                break;

            case CoreConstants.ModelProviders.Ollama:
                InjectOllamaStrategy(options, _ollamaNumCtx);
                break;
        }
    }

    // Anthropic 策略

    private static void InjectAnthropicStrategy(
        IList<ChatMessage> messages, ChatOptions? options)
    {
        // 1. Prompt Caching：标记 system 消息 + 最后一条 user 消息
        // 使用 JsonElement 构造强类型 JSON，避免依赖 SDK 对匿名对象的序列化行为
        var systemMsg = messages.FirstOrDefault(m => m.Role == ChatRole.System);
        if (systemMsg != null)
        {
            systemMsg.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            systemMsg.AdditionalProperties["cache_control"] = JsonElementCacheControlEphemeral1h;
        }

        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser != null && lastUser != systemMsg)
        {
            lastUser.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            lastUser.AdditionalProperties["cache_control"] = JsonElementCacheControlEphemeral;
        }

        // 2. Extended Thinking：从内部 key "thinking_budget" 映射到
        //    Anthropic thinking: {type:"enabled", budget_tokens:N}
        if (options?.AdditionalProperties == null) return;

        options.AdditionalProperties.TryGetValue("thinking_budget", out var budgetObj);
        if (budgetObj is int budget && budget > 0)
        {
            options.AdditionalProperties["thinking"] = BuildThinkingElement(budget);
        }
    }

    // 预构造的 JsonElement，避免每次调用时重复序列化
    private static readonly JsonElement JsonElementCacheControlEphemeral1h =
        JsonSerializer.SerializeToElement(new { type = "ephemeral", ttl = "1h" });

    private static readonly JsonElement JsonElementCacheControlEphemeral =
        JsonSerializer.SerializeToElement(new { type = "ephemeral" });

    private static JsonElement BuildThinkingElement(int budgetTokens) =>
        JsonSerializer.SerializeToElement(new { type = "enabled", budget_tokens = budgetTokens });

    private static void InjectOpenAIStrategy(ChatOptions? options)
    {
        // Prompt Caching：OpenAI 自动前缀缓存，无需任何参数 → 不做操作

        // Reasoning：从内部 key "thinking_effort" 映射到 reasoning_effort
        if (options?.AdditionalProperties == null) return;

        options.AdditionalProperties.TryGetValue("thinking_effort", out var effortObj);
        if (effortObj is string effort && !string.IsNullOrEmpty(effort))
        {
            options.AdditionalProperties["reasoning_effort"] = effort;
        }

        options.AdditionalProperties.Remove("thinking_budget");
    }

    private static void InjectOllamaStrategy(ChatOptions? options, int? numCtx)
    {
        if (options is null)
            return;

        // Native Ollama accepts request-level options. These are intentionally
        // applied here, after generic agent options have been assembled.
        if (numCtx is > 0)
            options.AddOllamaOption(OllamaOption.NumCtx, numCtx.Value);

        if (options.AdditionalProperties?.TryGetValue("thinking_effort", out var effort) == true
            && effort is string effortLevel
            && !string.IsNullOrWhiteSpace(effortLevel))
        {
            options.AddOllamaOption(OllamaOption.Think, effortLevel);
        }
        else if (options.AdditionalProperties?.TryGetValue("thinking_budget", out var budget) == true
            && budget is int budgetTokens
            && budgetTokens > 0)
        {
            options.AddOllamaOption(OllamaOption.Think, true);
        }

        options.AdditionalProperties?.Remove("thinking_budget");
        options.AdditionalProperties?.Remove("thinking_effort");
    }
}
