using Microsoft.Extensions.AI;
using OneCode.Core.Hooks;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：在模型调用边界触发 <see cref="HookInterceptionPoint.PreModelCall"/> /
/// <see cref="HookInterceptionPoint.PostModelCall"/> 两个拦截点。
///
/// <para><b>纯审计语义</b>：model 拦截点不支持 deny——fire 的聚合结果被丢弃，不阻断、
/// 不改写模型调用。需要阻断的策略应挂在 input / pre_tool_call / output 拦截点（见 docs/hooks.md）。</para>
///
/// <para><b>接线位置</b>：由 <c>AgentPipelineBuilder.BuildHarnessAgent</c> 在构造
/// <c>HarnessAgent</c> 前包装 <c>options.ChatClient</c>——位于 Harness 自行装配的
/// <c>FunctionInvokingChatClient</c> 下方。</para>
///
/// <para><b>旁路语义</b>：流式交付永不缓冲——update 逐条透传，post 审计所需的完整响应
/// 以旁路副本收集。是否有活跃 hook 经 <see cref="IHookExecutionService.HasActiveHooks"/>
/// 实时查询（与 <c>FireAsync</c> 前置过滤同源，热重载后天然一致）：无匹配时连 payload
/// 构造与投影收集也一并跳过，不改变逐 token 体验。</para>
///
/// <para><b>取消语义</b>：<see cref="OperationCanceledException"/> 一律透传，不降级为 hook 结果。</para>
/// </summary>
public sealed class ModelCallHookDecorator(IChatClient inner, IHookExecutionService hookExecutionService) : IChatClient
{
    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IHookExecutionService _hooks =
        hookExecutionService ?? throw new ArgumentNullException(nameof(hookExecutionService));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_hooks.HasActiveHooks(HookInterceptionPoint.PreModelCall, options?.ModelId))
            await FirePreAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var response = await _inner.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        if (_hooks.HasActiveHooks(HookInterceptionPoint.PostModelCall, options?.ModelId))
            await FirePostAsync(response, options?.ModelId, cancellationToken).ConfigureAwait(false);

        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_hooks.HasActiveHooks(HookInterceptionPoint.PreModelCall, options?.ModelId))
            await FirePreAsync(messages, options, cancellationToken).ConfigureAwait(false);

        // 零缓冲：update 逐条透传，交付不被延迟；post 审计所需的完整响应以旁路副本收集。
        List<ChatResponseUpdate>? updates =
            _hooks.HasActiveHooks(HookInterceptionPoint.PostModelCall, options?.ModelId) ? [] : null;

        await foreach (var update in _inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            updates?.Add(update);
            yield return update;
        }

        if (updates is { Count: > 0 })
            await FirePostAsync(updates.ToChatResponse(), options?.ModelId, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>pre_model_call 审计触发；聚合结果按纯审计语义丢弃（不做阻断）。</summary>
    private Task FirePreAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var payload = new HookPayload
        {
            Point = HookInterceptionPoint.PreModelCall,
            ModelId = options?.ModelId,
            RequestMessages = ProjectMessages(messages),
        };

        return _hooks.FireAsync(payload, actualMatcherValue: options?.ModelId, ct);
    }

    /// <summary>post_model_call 审计触发；聚合结果按纯审计语义丢弃（不做阻断）。</summary>
    private Task FirePostAsync(ChatResponse response, string? requestedModelId, CancellationToken ct)
    {
        var modelId = requestedModelId ?? response.ModelId;
        var payload = new HookPayload
        {
            Point = HookInterceptionPoint.PostModelCall,
            ModelId = modelId,
            ModelResponse = response.Text,
            FinishReason = response.FinishReason?.ToString(),
        };

        return _hooks.FireAsync(payload, actualMatcherValue: modelId, ct);
    }

    /// <summary>
    /// 把请求消息投影为稳定 JSON：只暴露 role / text，不携带原始对象图，
    /// 使 Core 层契约与外部 hook 进程（stdin JSON）看到同一形状。
    /// </summary>
    private static JsonElement? ProjectMessages(IEnumerable<ChatMessage> messages)
    {
        var projected = messages.Select(m => new
        {
            role = m.Role.Value,
            text = m.Text,
        });

        return JsonSerializer.SerializeToElement(projected);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose() => (_inner as IDisposable)?.Dispose();
}
