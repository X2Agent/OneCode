using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
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
public sealed class EditTransactionMiddleware
{
    private readonly EditTransaction _transaction;
    private readonly string _workingDirectory;
    private readonly Action<FileChange>? _onFileChange;
    private readonly ILogger? _logger;

    public EditTransactionMiddleware(
        EditTransaction transaction,
        string workingDirectory,
        Action<FileChange>? onFileChange = null,
        ILogger? logger = null)
    {
        _transaction = transaction;
        _workingDirectory = workingDirectory;
        _onFileChange = onFileChange;
        _logger = logger;
    }

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
                var path = ToolArgumentExtractor.ExtractFilePath(ctx.Arguments);
                if (path is not null)
                {
                    editedPath = Path.GetFullPath(path, _workingDirectory);
                    _transaction.Snapshot(editedPath);

                    // S-04: 在写入前把文件意图（路径 + 前值内容）持久化到 Operation Ledger，
                    // 消除"写入已发生、receipt 未持久化"的崩溃窗口——崩溃后可凭 receipt 回滚残留。
                    if (_transaction.Persistence is { IsEnabled: true } persistence)
                    {
                        var before = TryReadFileBytes(editedPath);
                        await persistence.Ledger!.AddFileIntentAsync(
                            persistence.OperationId!,
                            persistence.FencingToken,
                            editedPath,
                            before,
                            ct).ConfigureAwait(false);
                    }

                    // 读取编辑前的当前内容（用于增量 Diff，与 EditTransaction 的快照独立）
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

    private static byte[]? TryReadFileBytes(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var fileSize = new FileInfo(path).Length;
            if (fileSize > PathsHelper.MaxFileReadSize)
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
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
