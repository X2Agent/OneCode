using OneCode.Core.Tools;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

/// <summary>
/// fork 级账本接线（P10 反证）：GOAL 步内并行 DAG 的 fork 写入此前不入账本，
/// 崩溃后新世代的 ReconcileRunAsync 无法回滚孤儿写入。fork 事务挂到同一 run 账本
/// 前缀后，残留必须被世代清扫幂等还原；已提交的 fork 事务必须豁免。
/// </summary>
public sealed class FencedLedgerTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-fork-ledger-tests", Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public FencedLedgerTransactionTests()
    {
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "workspace", "code.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "before");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private FileOperationLedger CreateLedger() => new(Path.Combine(_root, "ledger"));

    private static ForkLedgerScope Scope(IOperationLedger ledger, string operationId = "goal/run-1/step/1/fence/7")
        => new(ledger, "goal/run-1", operationId, FencingToken: 7);

    [Fact]
    public async Task BeginForkAsync_BindsPersistenceAndRegistersActiveTransaction()
    {
        var ledger = CreateLedger();
        using var transaction = new EditTransaction();

        await FencedLedgerTransaction.BeginForkAsync(ledger, Scope(ledger), transaction);

        transaction.Persistence.Should().NotBeNull();
        transaction.Persistence!.IsEnabled.Should().BeTrue("fork 的写入 intent 必须经持久化上下文落账");
        var record = await ledger.LoadAsync(Scope(ledger).OperationId);
        record.Should().NotBeNull();
        record!.IsCommitted.Should().BeFalse("fork 事务开启后为 Active，成功才提交");
    }

    [Fact]
    public async Task ForkTransaction_LeftActiveByCrash_IsRestoredByRunReconcile()
    {
        // 反证：模拟 fork 崩溃——intent 已落账、文件已写、事务未提交。
        var ledger = CreateLedger();
        var scope = Scope(ledger);
        using var transaction = new EditTransaction();
        await FencedLedgerTransaction.BeginForkAsync(ledger, scope, transaction);
        await ledger.AddFileIntentAsync(scope.OperationId, scope.FencingToken, _file, "before"u8.ToArray());

        File.WriteAllText(_file, "fork-write");

        var results = await ledger.ReconcileRunAsync("goal/run-1");
        results.Should().ContainSingle("崩溃 fork 的 Active 事务必须被世代清扫");
        File.ReadAllText(_file).Should().Be("before", "孤儿写入必须还原到 before-content");
    }

    [Fact]
    public async Task CommittedForkTransaction_SurvivesRunReconcile()
    {
        var ledger = CreateLedger();
        var scope = Scope(ledger);
        using var transaction = new EditTransaction();
        await FencedLedgerTransaction.BeginForkAsync(ledger, scope, transaction);
        await ledger.AddFileIntentAsync(scope.OperationId, scope.FencingToken, _file, "before"u8.ToArray());

        // fork 成功：账本提交先于 fork 事务提交（RunForkedAgentAsync 的次序契约）。
        await ledger.CommitTransactionAsync(scope.OperationId, scope.FencingToken, "fork:test");
        transaction.Commit();
        File.WriteAllText(_file, "fork-write");

        await ledger.ReconcileRunAsync("goal/run-1");

        File.ReadAllText(_file).Should().Be("fork-write", "已提交的 fork 写入不得被清扫回滚");
    }

    [Fact]
    public async Task StepLevelBeginAsync_SweepsStaleForkTransactions()
    {
        // 上一世代残留的 fork 事务（同前缀、未提交）由 step 级 BeginAsync 独占清扫。
        var ledger = CreateLedger();
        var staleScope = Scope(ledger, "goal/run-1/step/1/fence/3");
        using (var staleTransaction = new EditTransaction())
        {
            await FencedLedgerTransaction.BeginForkAsync(ledger, staleScope, staleTransaction);
            await ledger.AddFileIntentAsync(
                staleScope.OperationId, staleScope.FencingToken, _file, "before"u8.ToArray());
        }
        File.WriteAllText(_file, "stale-generation");

        var currentTransaction = new EditTransaction();
        await FencedLedgerTransaction.BeginAsync(
            ledger, "goal/run-1", "goal/run-1/step/1/fence/7", 7, currentTransaction);

        File.ReadAllText(_file).Should().Be("before", "新世代开启必须回滚上一世代的 fork 残留");
        currentTransaction.Persistence!.IsEnabled.Should().BeTrue("step 级事务照常绑定");
    }
}
