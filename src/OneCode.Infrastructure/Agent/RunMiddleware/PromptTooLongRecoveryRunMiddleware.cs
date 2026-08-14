using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Infrastructure.Agent.RunMiddleware;

/// <summary>
/// Agent Run 级 PromptTooLong 恢复中间件 — 在 MAF Agent Run 层拦截
/// <see cref="PromptTooLongException"/> 和 HTTP 413 异常，执行恢复后重试。
///
/// <para>
/// <b>MAF 最佳实践对齐</b>（依据官方文档 agent-pipeline / agent-vs-run-scope / defining-middleware）：
/// <list type="bullet">
///   <item><b>层次定位</b>：PromptTooLong 是模型推理阶段的异常，直接冒泡到
///   <c>agent.RunAsync</c>/<c>agent.RunStreamingAsync</c>，属于 <b>Agent Run 级</b>关注点。
///   Run middleware 包裹 <c>innerAgent.RunAsync</c>，在异常层处理恢复，而非在 Runner 层重建 pipeline。</item>
///   <item><b>Agent-level 注册</b>：在 <see cref="AgentPipelineBuilder.Build"/> 中注册，
///   对所有 run 生效（Main/Worker/Team/Goal 自动获得恢复能力）。</item>
///   <item><b>不重建 pipeline</b>：Run middleware 收到的是已构建的 <c>innerAgent</c>，
///   无法重建。恢复策略为：fire hooks + 截断消息历史 + 重试，而非切换 CompactionProvider/模型。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>恢复策略</b>：
/// <list type="number">
///   <item>catch <see cref="PromptTooLongException"/> 或 HTTP 413 异常</item>
///   <item>fire <see cref="HookEvent.PreCompact"/> hook</item>
///   <item>截断消息历史：保留 system 消息 + 最近 N 条消息（默认 6）</item>
///   <item>retry <c>innerAgent.RunAsync</c> with truncated messages</item>
///   <item>fire <see cref="HookEvent.PostCompact"/> hook</item>
/// </list>
/// 最大重试 3 次，耗尽后抛出 <see cref="PromptTooLongException"/>。
/// </para>
///
/// <para>
/// <b>流式路径处理</b>：PromptTooLong 异常通常发生在流开始前（provider 校验 prompt 长度），
/// 此时无 update 已 yield。中间件在无 update yield 的前提下重试；
/// 若已有 update yield（mid-stream 异常，罕见），则直接抛出不重试（无法撤回已 yield 的 update）。
/// </para>
/// </summary>
public static class PromptTooLongRecoveryRunMiddleware
{
    /// <summary>默认最大重试次数。</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>截断后保留的最近消息数（不含 system 消息）。</summary>
    public const int DefaultKeepLastMessages = 6;

    /// <summary>
    /// 创建 Agent Run 级中间件的 (runFunc, runStreamingFunc) 委托对。
    /// </summary>
    /// <param name="hookExecutionService">
    /// Hook 执行服务（可选）。非 null 时在恢复前后 fire PreCompact/PostCompact hook。
    /// </param>
    /// <param name="logger">日志器（可选）。</param>
    /// <param name="maxAttempts">最大重试次数（含首次），默认 3。</param>
    /// <param name="keepLastMessages">截断后保留的最近消息数，默认 6。</param>
    /// <returns>(runFunc, runStreamingFunc) 委托对。</returns>
    public static (
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>
        ) Create(
            IHookExecutionService? hookExecutionService,
            ILogger? logger,
            int maxAttempts = DefaultMaxAttempts,
            int keepLastMessages = DefaultKeepLastMessages)
    {
        return (RunCore, RunStreamingCore);

        async Task<AgentResponse> RunCore(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent agent,
            CancellationToken ct)
        {
            var currentMessages = messages;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    return await agent.RunAsync(currentMessages, session, options, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPromptTooLong(ex))
                {
                    // Last attempt — let the exception propagate to the caller
                    if (attempt >= maxAttempts - 1)
                        throw;

                    logger?.LogWarning(
                        "PromptTooLong recovery: attempt {Attempt}/{Max}, truncating messages and retrying. Reason: {Reason}",
                        attempt + 1, maxAttempts, ex.Message);

                    await FireHookAsync(hookExecutionService, logger, HookEvent.PreCompact, ct)
                        .ConfigureAwait(false);

                    currentMessages = TruncateMessages(currentMessages, keepLastMessages);

                    await FireHookAsync(hookExecutionService, logger, HookEvent.PostCompact, ct)
                        .ConfigureAwait(false);
                }
            }

            // Unreachable: the loop either returns (success) or throws (last attempt).
            // Present to satisfy compiler return-path analysis.
            throw new InvalidOperationException("PromptTooLongRecoveryRunMiddleware: unreachable");
        }

        async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCore(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent agent,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var currentMessages = messages;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var yieldedAny = false;
                var enumerator = agent.RunStreamingAsync(currentMessages, session, options, ct)
                    .GetAsyncEnumerator(ct);

                try
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (IsPromptTooLong(ex) && !yieldedAny)
                        {
                            // Last attempt — let the exception propagate
                            if (attempt >= maxAttempts - 1)
                                throw;

                            // PromptTooLong before any update yielded — can retry
                            logger?.LogWarning(
                                "PromptTooLong streaming recovery: attempt {Attempt}/{Max}, truncating messages and retrying. Reason: {Reason}",
                                attempt + 1, maxAttempts, ex.Message);

                            await FireHookAsync(hookExecutionService, logger, HookEvent.PreCompact, ct)
                                .ConfigureAwait(false);

                            currentMessages = TruncateMessages(currentMessages, keepLastMessages);

                            await FireHookAsync(hookExecutionService, logger, HookEvent.PostCompact, ct)
                                .ConfigureAwait(false);

                            break; // break out of while, continue to next attempt
                        }

                        if (!hasNext)
                            yield break; // success — stream completed

                        yield return enumerator.Current;
                        yieldedAny = true;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            // Unreachable: the loop either yield-breaks (success) or throws (last attempt).
            // Present to satisfy compiler return-path analysis.
            throw new InvalidOperationException("PromptTooLongRecoveryRunMiddleware: unreachable");
        }
    }

    /// <summary>
    /// 判断异常是否为 PromptTooLong。覆盖三种检测维度：
    /// <list type="bullet">
    ///   <item><see cref="PromptTooLongException"/>（项目自定义异常）</item>
    ///   <item><see cref="HttpRequestException"/> with HTTP 413 或关键词</item>
    ///   <item>异常消息包含 prompt-too-long 关键词</item>
    /// </list>
    /// </summary>
    internal static bool IsPromptTooLong(Exception ex)
    {
        if (ex is PromptTooLongException)
            return true;

        if (ex is HttpRequestException httpEx && PromptTooLongDetector.IsPromptTooLong(httpEx))
            return true;

        return PromptTooLongDetector.IsPromptTooLong(ex);
    }

    /// <summary>
    /// 截断消息历史：保留所有 system 消息 + 最近 <paramref name="keepLast"/> 条非 system 消息。
    /// 用于 PromptTooLong 恢复时减少 token 数。
    /// </summary>
    internal static List<ChatMessage> TruncateMessages(IEnumerable<ChatMessage> messages, int keepLast)
    {
        var list = messages.ToList();
        if (list.Count <= keepLast)
            return list;

        var result = new List<ChatMessage>();

        // 保留所有 system 消息（通常在开头）
        var systemMessages = list.TakeWhile(m => m.Role == ChatRole.System).ToList();
        var remaining = list.Skip(systemMessages.Count).ToList();

        result.AddRange(systemMessages);

        // 保留最近的 N 条消息
        var skipCount = Math.Max(0, remaining.Count - keepLast);
        result.AddRange(remaining.Skip(skipCount));

        return result;
    }

    private static async Task FireHookAsync(
        IHookExecutionService? hookService,
        ILogger? logger,
        HookEvent @event,
        CancellationToken ct)
    {
        if (hookService is null)
            return;

        var payload = new HookPayload
        {
            Event = @event,
            Cwd = Environment.CurrentDirectory,
        };

        try
        {
            await hookService.FireAsync(payload, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Hook 失败不应阻断恢复流程，但必须记录以便诊断
            logger?.LogWarning(ex, "Hook {Event} failed during PromptTooLong recovery, continuing", @event);
        }
    }
}
