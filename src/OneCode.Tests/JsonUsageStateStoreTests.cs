using OneCode.Core.Session;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Tests;

public sealed class JsonUsageStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-usage-" + Guid.NewGuid().ToString("N"));

    public JsonUsageStateStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RecordBatch_ThenLoad_RoundTripsBucketsAndTotal()
    {
        var store = new JsonUsageStateStore(_root);
        var sessionId = SessionId.NewId();

        store.RecordBatch([new UsageBatchItem(sessionId, 1200, 350, 800, 100, 930)]);

        var loaded = store.Load();
        loaded.TotalBillableTokens.Should().Be(930);
        var bucket = loaded.Sessions.Should().ContainKey(sessionId).WhoseValue;
        bucket.TotalInputTokens.Should().Be(1200);
        bucket.TotalOutputTokens.Should().Be(350);
        bucket.TotalCacheReadTokens.Should().Be(800);
        bucket.TotalCacheWriteTokens.Should().Be(100);
    }

    [Fact]
    public void Load_CorruptMainFile_RestoresLastValidBackup()
    {
        var store = new JsonUsageStateStore(_root);
        var sessionId = SessionId.NewId();
        store.RecordBatch([new UsageBatchItem(sessionId, 10, 5, 0, 0, 15)]);
        store.RecordBatch([new UsageBatchItem(sessionId, 5, 5, 0, 0, 10)]);
        File.WriteAllText(GetUsageFile(), "{ not valid json");

        var state = store.Load();

        state.TotalBillableTokens.Should().Be(15);
        state.Sessions[sessionId].TotalInputTokens.Should().Be(10);
    }

    [Fact]
    public void Load_CorruptFileWithoutBackup_FailsInsteadOfResettingBudget()
    {
        _ = new JsonUsageStateStore(_root);
        File.WriteAllText(GetUsageFile(), "{ not valid json");
        var store = new JsonUsageStateStore(_root);

        var load = () => store.Load();

        load.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState()
    {
        var state = new JsonUsageStateStore(_root).Load();

        state.TotalBillableTokens.Should().Be(0);
        state.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task TokenLedger_HydratesPersistedState_AcrossRestart()
    {
        var store = new JsonUsageStateStore(_root);
        await using var first = new TokenLedger(store);
        var sessionId = SessionId.NewId();

        first.RecordUsage(sessionId, new UsageRecord("model", 1000, 200, 500, 50));
        await first.FlushAsync(TestContext.Current.CancellationToken);

        await using var restarted = new TokenLedger(store);
        restarted.GetTotalTokens().Should().Be(800);
        restarted.GetSessionUsage(sessionId)!.TotalInputTokens.Should().Be(1000);
        restarted.GetSessionUsage(sessionId)!.TotalOutputTokens.Should().Be(200);
        restarted.GetSessionUsage(sessionId)!.TotalCacheReadTokens.Should().Be(500);
        restarted.GetSessionUsage(sessionId)!.TotalCacheWriteTokens.Should().Be(50);
    }

    [Fact]
    public async Task TokenLedger_RemoveSession_PersistsDeletionAcrossRestart()
    {
        var store = new JsonUsageStateStore(_root);
        await using var ledger = new TokenLedger(store);
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord("model", 10, 5));

        ledger.RemoveSession(sessionId);

        await using var restarted = new TokenLedger(store);
        restarted.GetSessionUsage(sessionId).Should().BeNull();
        restarted.GetTotalTokens().Should().Be(15, "删除前先 flush，进程总量不回扣未落盘的历史增量");
    }

    [Fact]
    public async Task TokenLedger_RecordUsageAfterRemoveSession_RejectsLateWriter()
    {
        await using var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord("model", 10, 5));
        ledger.RemoveSession(sessionId);

        var record = () => ledger.RecordUsage(sessionId, new UsageRecord("model", 5, 5));

        record.Should().Throw<SessionDeletedException>().WithMessage("*deleted*");
    }

    [Fact]
    public async Task TokenLedger_TwoStoreInstances_RecordsEveryIncrement()
    {
        await using var first = new TokenLedger(new JsonUsageStateStore(_root));
        await using var second = new TokenLedger(new JsonUsageStateStore(_root));
        var firstSession = SessionId.NewId();
        var secondSession = SessionId.NewId();

        first.RecordUsage(firstSession, new UsageRecord("model", 10, 5));
        second.RecordUsage(secondSession, new UsageRecord("model", 20, 5));
        first.RecordUsage(firstSession, new UsageRecord("model", 5, 5));
        await first.FlushAsync(TestContext.Current.CancellationToken);
        await second.FlushAsync(TestContext.Current.CancellationToken);

        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(50);
        restarted.GetSessionUsage(firstSession)!.TotalAllTokens.Should().Be(25);
        restarted.GetSessionUsage(secondSession)!.TotalAllTokens.Should().Be(25);
    }

    [Fact]
    public async Task TokenLedger_ConcurrentRecordUsage_DoesNotOverwriteNewerSnapshot()
    {
        await using var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();

        Parallel.For(0, 40, _ => ledger.RecordUsage(sessionId, new UsageRecord("model", 2, 1)));
        await ledger.FlushAsync(TestContext.Current.CancellationToken);

        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(120);
        restarted.GetSessionUsage(sessionId)!.TotalAllTokens.Should().Be(120);
    }

    [Fact]
    public void TokenLedger_WithoutStore_StaysInMemory()
    {
        var ledger = new TokenLedger();
        var sessionId = SessionId.NewId();

        ledger.RecordUsage(sessionId, new UsageRecord("model", 10, 5));

        ledger.GetTotalTokens().Should().Be(15);
        ledger.GetSessionUsage(sessionId)!.TotalInputTokens.Should().Be(10);
    }

    [Fact]
    public void RecordBatch_PreservesPerRecordBillableRounding()
    {
        var store = new JsonUsageStateStore(_root);
        var sessionId = SessionId.NewId();

        store.RecordBatch([
            new UsageBatchItem(sessionId, 5, 1, 5, 0, 1),
            new UsageBatchItem(sessionId, 5, 1, 5, 0, 1),
        ]);

        var state = store.Load();
        state.TotalBillableTokens.Should().Be(2,
            "两条缓存读各 5 的增量应分别折算，不能聚合成 10 后多算 1 个 token");
        state.Sessions[sessionId].TotalInputTokens.Should().Be(10);
        state.Sessions[sessionId].TotalOutputTokens.Should().Be(2);
    }

    [Fact]
    public void RecordBatch_DeletedSession_SkipsLateIncrementWithoutBlockingOthers()
    {
        var store = new JsonUsageStateStore(_root);
        var deleted = SessionId.NewId();
        var active = SessionId.NewId();
        store.RecordBatch([new UsageBatchItem(deleted, 10, 5, 0, 0, 15)]);
        store.RemoveSession(deleted);

        store.RecordBatch([
            new UsageBatchItem(deleted, 100, 50, 0, 0, 150),
            new UsageBatchItem(active, 10, 5, 0, 0, 15),
        ]);

        var state = store.Load();
        state.Sessions.Should().NotContainKey(deleted);
        state.Sessions[active].TotalInputTokens.Should().Be(10);
        state.TotalBillableTokens.Should().Be(30, "删除会话的 150 增量被丢弃，历史 15 与活跃会话 15 保留");
    }

    private string GetUsageFile()
        => Path.Combine(_root, Constants.App.ConfigDirName, Constants.Subdirs.Usage, "usage.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
