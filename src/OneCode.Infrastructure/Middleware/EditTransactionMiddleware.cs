using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Text;
using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// MAF Agent Middleware：在 Write/Edit 工具执行前快照目标文件，
/// 配合 EditTransaction 实现多文件编辑事务。
///
/// 工具执行顺序不变（仍即时落盘），但 EditTransaction 在异常/取消时回滚所有修改。
///
/// 工作原理：
///   1. 每次 Write/Edit 调用前 → Snapshot(filePath)
///   2. Agent 成功完成 → EditTransaction.Commit()（丢弃快照）
///   3. Agent 异常/取消 → EditTransaction.Dispose() → Rollback()（恢复文件）
///
/// 文件变更通知：
///   每次文件编辑后，计算增量 Diff 并通过 <c>OnFileChange</c> 回调发射
///   <see cref="FileChange"/> 事件，供 TUI 层实时渲染 Diff 块。
/// </summary>
public sealed class EditTransactionMiddleware(
    EditTransaction transaction,
    Func<string> resolveWorkingDirectory,
    Action<FileChange>? onFileChange = null,
    ILogger? logger = null)
{
    private readonly EditTransaction _transaction = transaction;
    private readonly Func<string> _resolveWorkingDirectory = resolveWorkingDirectory;
    private readonly Action<FileChange>? _onFileChange = onFileChange;
    private readonly ILogger? _logger = logger;

    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            var isFileEdit = ctx.Function is not null && ToolNames.IsFileEditTool(ctx.Function.Name);
            string? editedPath = null;
            string? contentBefore = null;

            // 仅拦截文件编辑类工具
            if (isFileEdit)
            {
                // 目标路径按工具 schema 解析，与安全层/权限层共用同一结论，不再按参数名猜测。
                var parameters = ToolArgumentExtractor.ToParameterDictionary(ctx.Arguments, ctx.Function!.Name, _logger);
                var path = parameters is null
                    ? null
                    : ToolTargetPathResolver.ResolvePrimaryPath(ctx.Function, parameters);
                if (path is not null)
                {
                    // 每次调用解析活动目录：同 run 内 EnterWorktree 切目录后，
                    // 快照/回滚目标与工具实际写入保持同一棵目录树。
                    editedPath = Path.GetFullPath(path, _resolveWorkingDirectory());
                    _transaction.Snapshot(editedPath);

                    // 仅在需要发射增量 Diff 通知时读取编辑前文本，避免无回调路径上的冗余文件读取。
                    if (_onFileChange is not null)
                        contentBefore = TryReadFileText(editedPath);
                }
            }

            var result = await next(ctx, ct).ConfigureAwait(false);

            // 编辑后计算增量 Diff 并通知 TUI
            if (isFileEdit && editedPath is not null && _onFileChange is not null)
            {
                TryEmitFileChange(editedPath, contentBefore);
            }

            return result;
        };
    }

    /// <summary>
    /// 读取编辑后的文件内容，与编辑前内容对比，计算增量 Diff 并发射 FileChange。
    /// </summary>
    private void TryEmitFileChange(string editedPath, string? contentBefore)
    {
        if (_onFileChange is null) return;

        try
        {
            var contentAfter = TryReadFileText(editedPath);
            if (contentAfter is null) return;

            var (added, removed) = UnifiedDiff.ComputeLineChanges(
                contentBefore ?? "",
                contentAfter);

            if (added.Length == 0 && removed.Length == 0)
                return;

            var fileName = Path.GetFileName(editedPath);
            _onFileChange(new FileChange(fileName, added, removed));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Diff computation failed for {Path}, skipping file change event", editedPath);
        }
    }

    private string? TryReadFileText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read file {Path} for snapshot", path);
            return null;
        }
    }
}
