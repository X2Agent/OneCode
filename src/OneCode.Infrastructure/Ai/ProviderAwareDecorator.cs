using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using CoreConstants = OneCode.Core.Constants;
using Ttl = Anthropic.Models.Messages.Ttl;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：根据构造时确定的提供商，补齐各 provider 原生请求参数。
/// Provider 在 DI 注册时就已确定（Anthropic / OpenAI / Ollama），运行期间不变。
/// </summary>
/// <remarks>
/// 只允许使用 SDK 的<b>已文档化</b>扩展点：
///   - Anthropic prompt caching → <c>AIContentCacheExtensions.WithCacheControl</c>（键 <c>anthropic:cache_control</c>）
///   - Ollama 原生参数 → <c>AddOllamaOption</c>
/// 严禁在 <c>AdditionalProperties</c> 里手写 provider 私有 wire key（如裸的 <c>cache_control</c> /
/// <c>thinking</c> / <c>reasoning_effort</c>）：那既不是任何 SDK 的契约，也不会被读取，
/// 只会得到「看起来生效、实际静默失效」的假象。思考/推理意图统一走 MEAI 标准的
/// <see cref="ChatOptions.Reasoning"/>，由各适配器自行翻译。
/// </remarks>
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
        // 始终复制：Anthropic 策略会替换列表元素（clone-on-write），绝不能写回调用方的列表
        var messageList = messages.ToList();
        ApplyProviderStrategy(messageList, options);
        return await _inner.GetResponseAsync(messageList, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
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
                InjectAnthropicStrategy(messages);
                break;

            case CoreConstants.ModelProviders.Ollama:
                InjectOllamaStrategy(options, _ollamaNumCtx);
                break;

            // OpenAI：前缀缓存由服务端自动完成，推理强度走 ChatOptions.Reasoning，
            // 无需任何 provider 私有注入。
        }
    }

    // Anthropic 策略

    /// <summary>
    /// Anthropic prompt caching：把缓存断点打在<b>内容块</b>上。
    /// 断点标记的是「到此为止的前缀可缓存」，Anthropic 最多接受 4 个断点。
    ///   - system 消息（产品侧稳定前缀）→ <c>ttl=1h</c>
    ///   - 最后一条消息的最后一个可缓存块（对话前缀）→ 默认 5m
    /// </summary>
    /// <remarks>
    /// 主体正文经 <see cref="ChatOptions.Instructions"/> 下发，Anthropic 适配器把它拼成 system 块时
    /// 不带 cache_control，所以 1h 断点只在确实存在 system 消息时才有意义——这里不合成 system 消息，
    /// 否则会和 Instructions 里的正文重复。
    /// 断点写在消息<b>副本</b>上（clone-on-write，见 <see cref="WithCacheBreakpoint"/>），
    /// 每次请求都从干净实例重建，因此单个请求恰好只有上述 2 个断点。
    /// </remarks>
    private static void InjectAnthropicStrategy(IList<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var ttl = i == messages.Count - 1 ? Ttl.Ttl5m
                : messages[i].Role == ChatRole.System ? Ttl.Ttl1h
                : (Ttl?)null;
            if (ttl is not { } breakpointTtl)
                continue;

            var index = LastCacheableIndex(messages[i]);
            if (index < 0)
                continue;

            messages[i] = WithCacheBreakpoint(messages[i], index, breakpointTtl);
        }
    }

    /// <summary>
    /// 在消息<b>副本</b>的指定块上打缓存断点，返回副本，原消息及其内容实例不被写入。
    /// MAF 的历史层跨工具轮 / 跨 turn 复用同一批 <see cref="ChatMessage"/> / <see cref="AIContent"/>
    /// 实例；若原地写 <c>cache_control</c>，上一轮的「末条消息」断点会滞留在中间历史里并随轮数累积，
    /// 很快超过 Anthropic 的 4 断点上限 → 400。clone-on-write 保证历史原始实例永远干净。
    /// </summary>
    private static ChatMessage WithCacheBreakpoint(ChatMessage message, int index, Ttl ttl)
    {
        var contents = new List<AIContent>(message.Contents);
        contents[index] = CloneContent(contents[index]).WithCacheControl(ttl);

        var clone = new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation,
        };
        if (message.AdditionalProperties is { } props)
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(props);
        return clone;
    }

    private static AIContent CloneContent(AIContent content) => content switch
    {
        TextContent text => CopySharedState(new TextContent(text.Text), text),
        FunctionCallContent call => CopySharedState(new FunctionCallContent(call.CallId, call.Name, call.Arguments)
        {
            Exception = call.Exception,
            InformationalOnly = call.InformationalOnly,
        }, call),
        FunctionResultContent result => CopySharedState(
            new FunctionResultContent(result.CallId, result.Result) { Exception = result.Exception }, result),
        DataContent data => CopySharedState(
            !data.Data.IsEmpty
                ? new DataContent(data.Data.ToArray(), data.MediaType) { Name = data.Name }
                : new DataContent(data.Uri, data.MediaType) { Name = data.Name },
            data),
        _ => content,
    };

    private static T CopySharedState<T>(T clone, AIContent source) where T : AIContent
    {
        clone.RawRepresentation = source.RawRepresentation;
        if (source.AdditionalProperties is { } props)
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(props);
        if (source.Annotations is { Count: > 0 } annotations)
            clone.Annotations = new List<AIAnnotation>(annotations);
        return clone;
    }

    private static int LastCacheableIndex(ChatMessage message)
    {
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            if (IsCacheableContent(message.Contents[i]))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 内容类型是否支持 Anthropic cache_control（见 SDK 的 <c>WithCacheControlFrom</c> 调用点）。
    /// </summary>
    private static bool IsCacheableContent(AIContent content) => content switch
    {
        TextContent => true,
        FunctionCallContent => true,
        FunctionResultContent => true,
        DataContent data => data.HasTopLevelMediaType("image"),
        _ => false,
    };

    private static void InjectOllamaStrategy(ChatOptions? options, int? numCtx)
    {
        if (options is null)
            return;

        // Native Ollama accepts request-level options. These are intentionally
        // applied here, after generic agent options have been assembled.
        if (numCtx is > 0)
            options.AddOllamaOption(OllamaOption.NumCtx, numCtx.Value);

        // OllamaSharp 不读 MEAI 的 ChatOptions.Reasoning，这里把标准档位翻译成原生 think。
        if (options.Reasoning?.Effort is { } effort && effort != ReasoningEffort.None)
            options.AddOllamaOption(OllamaOption.Think, ToOllamaThinkLevel(effort));
    }

    private static string ToOllamaThinkLevel(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        _ => "high",
    };
}
