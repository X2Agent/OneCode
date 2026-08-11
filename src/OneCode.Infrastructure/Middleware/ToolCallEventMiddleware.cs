using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Coordinator;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 工具调用事件中间件：向 <see cref="AgentPipelineOptions.OrchestrationEventSink"/>
/// 发射 <see cref="OrchestrationEvent.ToolStart"/> / <see cref="OrchestrationEvent.ToolDone"/> 事件，
/// 供 TEAM 模式实时流式展示工具活动。
///
/// 放在权限中间件之前，确保即使工具被拒绝也能发射事件（拒绝体现在 ToolDone.IsError）。
///
/// 设计要点：
/// 1. IsError 检测 — 工具拒绝（PermissionChecker/Hook/SafetyInvariant 拒绝）通过返回
///    ToolResult.Error + ctx.Terminate=true 体现，不抛异常。必须在 result 拿到后立即判定
///    tr.IsError（或从 ToolExecutionContext 读取），否则 TEAM TUI 会把"被拒绝"显示为"成功"。
/// 2. sink 调用全部 try-catch 保护 — 订阅者异常不应掩盖原始工具异常或阻断流程。
/// 3. sink=null 在构建期 fail-closed，而非运行时 NRE。
/// 4. catch (Exception) 排除 OperationCanceledException 以保留取消传播。
/// </summary>
public static class ToolCallEventMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(AgentPipelineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var sink = options.OrchestrationEventSink
            ?? throw new ArgumentNullException(
                nameof(options) + "." + nameof(AgentPipelineOptions.OrchestrationEventSink));

        return async (agent, ctx, next, ct) =>
        {
            var agentName = agent.Name ?? "agent";
            if (ctx.Function is null)
                return await next(ctx, ct).ConfigureAwait(false);

            var toolName = ctx.Function.Name;
            var toolInput = ctx.Arguments is not null
                ? DisplayJsonSerializer.Serialize(ctx.Arguments)
                : null;
            var toolId = Guid.NewGuid().ToString("N")[..12];

            // ToolStart sink 失败不应阻断工具执行
            TrySink(new OrchestrationEvent.ToolStart(agentName, toolId, toolName, toolInput));

            var isError = false;
            string? resultStr = null;
            try
            {
                var result = await next(ctx, ct).ConfigureAwait(false);
                // 工具拒绝（PermissionChecker/Hook/SafetyInvariant 拒绝）通过返回
                // ToolResult.Error + ctx.Terminate=true 体现，不抛异常。必须在 result 拿到后
                // 立即判定错误，否则 TEAM TUI 会把"被拒绝"显示为"成功"。
                // 从结构化 context 读取 IsError（与 StateMachine 判定逻辑一致，
                // ToolResult 直接到达走 tr.IsError，string 走 context.IsError）。
                // stateBag 不可用（异常降级）时仅对 ToolResult 判定，string 无法可靠识别错误。
                var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
                isError = stateBag is not null
                    ? result is ToolResult tr ? tr.IsError : stateBag.GetOrInitializeToolExecutionContext().IsError
                    : result is ToolResult { IsError: true };
                resultStr = ExtractResultText(result);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                isError = true;
                resultStr = ex.Message;
                throw;
            }
            finally
            {
                TrySink(new OrchestrationEvent.ToolDone(agentName, toolName, isError, resultStr, toolInput, toolId));
            }
        };

        // sink 调用全部 try-catch 保护，避免订阅者异常掩盖原始工具异常或阻断流程
        void TrySink(OrchestrationEvent evt)
        {
            try { sink(evt); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "ToolCallEvent sink threw for event {EventType}", evt.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 从工具结果中提取可读文本。对 ToolResult 取 Content，对其他类型用 ToString()。
    /// 避免对 ToolResult 调 ToString() 返回全字段 dump 污染事件展示。
    /// </summary>
    private static string? ExtractResultText(object? result) => result switch
    {
        ToolResult tr => tr.Content,
        _ => result?.ToString()
    };
}
