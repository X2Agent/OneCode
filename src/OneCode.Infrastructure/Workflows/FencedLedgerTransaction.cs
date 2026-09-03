using OneCode.Core.Workflows;

namespace OneCode.Infrastructure.Workflows;

/// <summary>
/// S-04 ledger 序列共享封装——收敛 Build / Goal / Team 三处逐字同构的
/// 「新执行世代开启」序列：先回滚上一世代残留（<see cref="IOperationLedger.ReconcileRunAsync"/>），
/// 再开启本世代持久化事务（<see cref="IOperationLedger.BeginTransactionAsync"/>），
/// 并把文件编辑 intent 落盘（<see cref="Agent.EditTransaction.PersistTo"/>）。
/// 三个调用的顺序不可变：intent 必须先于任何文件写持久化，才能消除"已写未记"的崩溃窗口。
/// </summary>
public static class FencedLedgerTransaction
{
    /// <param name="ledger">操作 ledger 服务（跨进程互斥与 checkpoint 对账）。</param>
    /// <param name="runLedgerId">run 级前缀（如 <c>goal/{id}</c>），用于世代清扫回滚。</param>
    /// <param name="operationId">本世代事务标识（attempt / step / run 粒度由各模式决定）。</param>
    /// <param name="fencingToken">当前 Workflow lease 令牌。</param>
    /// <param name="transaction">承载本世代文件编辑的共享 EditTransaction。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task BeginAsync(
        IOperationLedger ledger,
        string runLedgerId,
        string operationId,
        long fencingToken,
        Agent.EditTransaction transaction,
        CancellationToken ct = default)
    {
        await ledger.ReconcileRunAsync(runLedgerId, ct).ConfigureAwait(false);
        await ledger.BeginTransactionAsync(
            operationId,
            "file-transaction",
            fencingToken,
            ct).ConfigureAwait(false);
        transaction.PersistTo(ledger, operationId, fencingToken);
    }
}
