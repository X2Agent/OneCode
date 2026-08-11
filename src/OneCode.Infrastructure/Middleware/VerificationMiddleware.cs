using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// Post-tool 验证中间件（Harness Layer 1）。
/// </summary>
/// <remarks>
/// 在 EditTransaction 之后拦截文件编辑工具，按防抖策略触发验证（编译/类型检查等）。
/// 验证失败时把错误回注 LLM 上下文（附加到工具结果末尾），强制下一轮修复。
///
/// 防抖策略：
/// <list type="bullet">
///   <item>每次源码文件编辑后递增 EditsSinceLastBuild（通过 StateBag 共享）</item>
///   <item>当编辑次数达到 <see cref="VerificationOptions.Threshold"/>（默认 3）时触发检查</item>
///   <item>检查后重置计数器</item>
///   <item>验证失败时返回 ToolResult.Error，由 StateMachineMiddleware 统一管理状态转换</item>
/// </list>
///
/// EditsSinceLastBuild 递增由此处独占（在 threshold 检查前递增），避免双写。
///
/// 插入位置：EditTransactionMiddleware 之后、ToolExecutionBudget 之前。
/// </remarks>
public sealed class VerificationMiddleware
{
    private readonly IVerificationProvider _provider;
    private readonly string _workingDirectory;
    private readonly VerificationOptions _options;
    private readonly ILogger _logger;

    public VerificationMiddleware(
        IVerificationProvider provider,
        string workingDirectory,
        VerificationOptions options,
        ILogger<VerificationMiddleware> logger)
    {
        _provider = provider;
        _workingDirectory = workingDirectory;
        _options = options;
        _logger = logger;
    }

    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            var result = await next(ctx, ct).ConfigureAwait(false);

            var isFileEdit = ctx.Function is not null && ToolNames.IsFileEditTool(ctx.Function.Name);
            if (!isFileEdit)
                return result;

            // 按 provider 判断是否为支持的源码文件（多语言路由）
            var editedPath = ToolArgumentExtractor.ExtractFilePath(ctx.Arguments);
            if (editedPath is null || !_provider.IsSourceFile(editedPath))
                return result;

            var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
            if (stateBag is null)
            {
                _logger.LogWarning("VerificationMiddleware: StateBag unavailable, skipping verification");
                return result;
            }

            // ModifiedFiles 需在此添加以确保当前编辑文件被纳入验证范围
            stateBag.GetOrInitializeModifiedFiles().Add(editedPath);

            // EditsSinceLastBuild 递增由此处独占（在 threshold 检查前递增）。
            stateBag.IncrementEditsSinceLastBuild();

            // 防抖：未达阈值不触发
            if (stateBag.GetEditsSinceLastBuild() < _options.Threshold)
                return result;

            // 触发验证
            var checkResult = await _provider.VerifyAsync(
                _workingDirectory,
                stateBag.GetOrInitializeModifiedFiles().ToList(),
                ct).ConfigureAwait(false);

            stateBag.ResetEditsSinceLastBuild();

            if (checkResult.Skipped)
            {
                _logger.LogDebug("Verification skipped (no matching profile or build tool)");
                return result;
            }

            if (checkResult.Success)
            {
                _logger.LogDebug("Verification passed after {Count} edits", _options.Threshold);
                return result;
            }

            // 写入 IsVerificationFailure=true，让 StateMachine 通过强类型字段
            // 触发 Active→Recovering 转移（编译错误需要立即修复，不等连续失败阈值）。
            stateBag.GetOrInitializeToolExecutionContext().IsVerificationFailure = true;

            _logger.LogWarning("Verification failed with {ErrorCount} errors", checkResult.Errors.Count);

            var errorSummary = checkResult.FormatForLlm();
            var existingResult = result as string ?? result?.ToString() ?? "";
            var combinedContent = $"{existingResult}\n\n[VERIFICATION ERROR]\n{errorSummary}";
            var suggestedAction = "Fix the verification errors above before making further edits.";

            return ToolResult.Error(combinedContent, suggestedAction);
        };
    }
}

/// <summary>
/// 验证中间件配置。
/// </summary>
public sealed record VerificationOptions
{
    /// <summary>每编辑多少次源码文件后触发一次验证。默认 3。</summary>
    public int Threshold { get; init; } = 3;

    public static readonly VerificationOptions Default = new();
}
