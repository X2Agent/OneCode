using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Middleware.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 行为契约中间件：在工具执行前后运行契约验证。
/// - Pre-condition 失败：阻止工具执行，返回恢复指导
/// - Post-condition 失败：仅记录日志，不修改结果。基础设施级断言不应计入 3-strike 阻断。
///
/// 本中间件不递增 ConsecutiveFailures（由 StateMachineMiddleware 独占）。
///
/// StateBag 不可用时直通 next（非 agent run 上下文，如单元测试）。
/// </summary>
public static class ContractMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(IReadOnlyList<FileEditContract> contracts,
            ILogger logger)
    {
        return async (_, ctx, next, ct) =>
        {
            if (ctx.Function is null)
                return await next(ctx, ct).ConfigureAwait(false);

            var toolName = ctx.Function.Name;
            var parameters = ToolArgumentExtractor.ToParameterDictionary(ctx.Arguments, toolName, logger);
            if (parameters is null)
            {
                // 参数解析失败时 fail-closed，但只失败当前调用以保留批次完整性。
                return ToolResult.Error(
                    $"[CONTRACT] Parameter extraction failed for tool '{toolName}' — blocked to prevent fail-open.");
            }

            var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
            if (stateBag is null)
            {
                logger.LogWarning("ContractMiddleware: StateBag unavailable, skipping contract checks");
                return await next(ctx, ct).ConfigureAwait(false);
            }

            // Pre-condition: 找到适用的契约并验证
            foreach (var contract in contracts.Where(c => c.ApplicableTools.Contains(toolName)))
            {
                var preResult = await contract.ValidatePreConditionsAsync(toolName, parameters, ct)
                    .ConfigureAwait(false);

                if (preResult is ContractFailed preFail)
                {
                    var guidance = contract.BuildRecoveryGuidance(preFail);
                    return ToolResult.Error(guidance);
                }
            }

            var result = await next(ctx, ct).ConfigureAwait(false);

            // Post-condition: 验证执行后条件
            foreach (var contract in contracts.Where(c => c.ApplicableTools.Contains(toolName)))
            {
                var postResult = await contract.ValidatePostConditionsAsync(
                    toolName, parameters, result, ct).ConfigureAwait(false);
                if (postResult is ContractFailed postFail)
                {
                    logger.LogWarning("Contract post-condition violated: {Contract} — {Desc}",
                        contract.GetType().Name, postFail.Description);
                }
            }

            return result;
        };
    }
}
