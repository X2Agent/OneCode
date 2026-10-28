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
    public void RecordUsage_ThenLoad_RoundTripsBucketsAndTotal()
    {
        var store = new JsonUsageStateStore(_root);
        var sessionId = SessionId.NewId();

        store.RecordUsage(sessionId, new UsageRecord("model", 1200, 350, 800, 100));

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
        store.RecordUsage(sessionId, new UsageRecord("model", 10, 5));
        store.RecordUsage(sessionId, new UsageRecord("model", 5, 5));
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
    public void TokenLedger_HydratesPersistedState_AcrossRestart()
    {
        var store = new JsonUsageStateStore(_root);
        var first = new TokenLedger(store);
        var sessionId = SessionId.NewId();

        first.RecordUsage(sessionId, new UsageRecord("model", 1000, 200, 500, 50));

        var restarted = new TokenLedger(store);
        restarted.GetTotalTokens().Should().Be(800);
        restarted.GetSessionUsage(sessionId)!.TotalInputTokens.Should().Be(1000);
        restarted.GetSessionUsage(sessionId)!.TotalOutputTokens.Should().Be(200);
        restarted.GetSessionUsage(sessionId)!.TotalCacheReadTokens.Should().Be(500);
        restarted.GetSessionUsage(sessionId)!.TotalCacheWriteTokens.Should().Be(50);
    }

    [Fact]
    public void TokenLedger_RemoveSession_PersistsDeletionAcrossRestart()
    {
        var store = new JsonUsageStateStore(_root);
        var ledger = new TokenLedger(store);
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord("model", 10, 5));

        ledger.RemoveSession(sessionId);

        var restarted = new TokenLedger(store);
        restarted.GetSessionUsage(sessionId).Should().BeNull();
        restarted.GetTotalTokens().Should().Be(15, "进程总量是累计口径，删除会话桶不回扣历史总量");
    }

    [Fact]
    public void TokenLedger_RecordUsageAfterRemoveSession_RejectsLateWriter()
    {
        var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord("model", 10, 5));
        ledger.RemoveSession(sessionId);

        var record = () => ledger.RecordUsage(sessionId, new UsageRecord("model", 5, 5));

        record.Should().Throw<InvalidOperationException>().WithMessage("*deleted*");
    }

    [Fact]
    public void TokenLedger_TwoStoreInstances_RecordsEveryIncrement()
    {
        var first = new TokenLedger(new JsonUsageStateStore(_root));
        var second = new TokenLedger(new JsonUsageStateStore(_root));
        var firstSession = SessionId.NewId();
        var secondSession = SessionId.NewId();

        first.RecordUsage(firstSession, new UsageRecord("model", 10, 5));
        second.RecordUsage(secondSession, new UsageRecord("model", 20, 5));
        first.RecordUsage(firstSession, new UsageRecord("model", 5, 5));

        var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(50);
        restarted.GetSessionUsage(firstSession)!.TotalAllTokens.Should().Be(25);
        restarted.GetSessionUsage(secondSession)!.TotalAllTokens.Should().Be(25);
    }

    [Fact]
    public void TokenLedger_ConcurrentRecordUsage_DoesNotOverwriteNewerSnapshot()
    {
        var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();

        Parallel.For(0, 40, _ => ledger.RecordUsage(sessionId, new UsageRecord("model", 2, 1)));

        var restarted = new TokenLedger(new JsonUsageStateStore(_root));
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

    private string GetUsageFile()
        => Path.Combine(_root, Constants.App.ConfigDirName, Constants.Subdirs.Usage, "usage.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
