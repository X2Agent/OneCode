using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// <see cref="ToolMetadata.IsConcurrencySafe"/> 为 false 的工具的运行时串行闸：
/// 同一 agent 管道内出现并行 tool_call（MEAI <c>AllowConcurrentInvocation</c> 开启，
/// 或多代理 DAG 并行写场景）时，非并发安全工具（Write/Edit/Delete 等）逐个执行，
/// 并发安全工具直接放行——元数据由「文档性」变为「约束性」。
/// MEAI 默认串行调用下本闸零争用；闸粒度是管道实例（一次 agent 装配），
/// 不同会话 / 不同 fork 之间互不阻塞。
/// </summary>
public sealed class UnsafeToolSerialGateMiddleware(ToolMetadataRegistry? metadata = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            // 未知工具（元数据缺失）不阻塞：并发约束只对已声明 IsConcurrencySafe=false 的工具生效，
            // 静态目录之外的 Harness provider 工具（todos_* 等会话内状态工具）天然并发安全。
            if (ctx.Function is null
                || metadata?.Get(ctx.Function.Name) is not { IsConcurrencySafe: false })
            {
                return await next(ctx, ct).ConfigureAwait(false);
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await next(ctx, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        };
    }
}
