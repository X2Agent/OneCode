using OneCode.Core.Tokens;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Tests;

public sealed class TokenLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-ledger-" + Guid.NewGuid().ToString("N"));

    public TokenLedgerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RecordUsage_MemoryIsImmediateAndFlushPersistsSameTotals()
    {
        await using var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord("model", 1000, 200, 500, 50));

        ledger.GetTotalTokens().Should().Be(800);
        var inMemory = ledger.GetSessionUsage(sessionId)!;
        inMemory.TotalInputTokens.Should().Be(1000);
        inMemory.TotalCacheReadTokens.Should().Be(500);

        await ledger.FlushAsync(TestContext.Current.CancellationToken);

        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(800);
        restarted.GetSessionUsage(sessionId)!.TotalInputTokens.Should().Be(1000);
        restarted.GetSessionUsage(sessionId)!.TotalOutputTokens.Should().Be(200);
        restarted.GetSessionUsage(sessionId)!.TotalCacheReadTokens.Should().Be(500);
        restarted.GetSessionUsage(sessionId)!.TotalCacheWriteTokens.Should().Be(50);
    }

    [Fact]
    public async Task FlushAsync_ConcurrentCalls_PreserveEveryIncrement()
    {
        await using var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var sessionId = SessionId.NewId();
        Parallel.For(0, 40, _ => ledger.RecordUsage(sessionId, new UsageRecord("model", 2, 1)));

        await Task.WhenAll(
            ledger.FlushAsync(TestContext.Current.CancellationToken),
            ledger.FlushAsync(TestContext.Current.CancellationToken),
            ledger.FlushAsync(TestContext.Current.CancellationToken));

        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(120);
        restarted.GetSessionUsage(sessionId)!.TotalAllTokens.Should().Be(120);
    }

    [Fact]
    public async Task FlushAsync_SessionDeletedByAnotherWriter_DropsOnlyThatSession()
    {
        var store = new JsonUsageStateStore(_root);
        await using var ledger = new TokenLedger(store);
        var deleted = SessionId.NewId();
        var active = SessionId.NewId();
        ledger.RecordUsage(deleted, new UsageRecord("model", 100, 50));
        ledger.RecordUsage(active, new UsageRecord("model", 10, 5));

        store.RemoveSession(deleted);
        await ledger.FlushAsync(TestContext.Current.CancellationToken);

        ledger.GetSessionUsage(deleted).Should().BeNull();
        ledger.GetSessionUsage(active)!.TotalInputTokens.Should().Be(10);
        ledger.GetTotalTokens().Should().Be(15);
        await using var restarted = new TokenLedger(store);
        restarted.GetSessionUsage(deleted).Should().BeNull();
        restarted.GetSessionUsage(active)!.TotalInputTokens.Should().Be(10);
        restarted.GetTotalTokens().Should().Be(15);
    }

    [Fact]
    public async Task Dispose_UnflushedIncrements_PersistForTheNextProcess()
    {
        var sessionId = SessionId.NewId();
        var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        ledger.RecordUsage(sessionId, new UsageRecord("model", 1000, 200, 500, 50));

        // 同步释放路径（DI 容器同步释放单例时走这里）：后台批次还在合并窗口内，
        // 只有释放时的收尾 flush 能让这笔增量落盘。
        ledger.Dispose();

        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetTotalTokens().Should().Be(800);
        restarted.GetSessionUsage(sessionId)!.TotalInputTokens.Should().Be(1000);
        restarted.GetSessionUsage(sessionId)!.TotalCacheReadTokens.Should().Be(500);
    }

    [Fact]
    public async Task RemoveSession_WhileAnotherSessionRecords_KeepsEveryOtherIncrementAndTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        var removed = SessionId.NewId();
        var kept = SessionId.NewId();
        ledger.RecordUsage(removed, new UsageRecord("model", 100, 50));

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                ledger.RecordUsage(kept, new UsageRecord("model", 2, 1));
        }, ct);
        ledger.RemoveSession(removed);
        await writer;
        await ledger.FlushAsync(ct);

        ledger.GetSessionUsage(removed).Should().BeNull();
        ledger.GetSessionUsage(kept)!.TotalAllTokens.Should().Be(600);
        ledger.GetTotalTokens().Should().Be(750, "删除会话不回扣已计入的 150，并发写入的 600 全部保留");
        await using var restarted = new TokenLedger(new JsonUsageStateStore(_root));
        restarted.GetSessionUsage(removed).Should().BeNull();
        restarted.GetSessionUsage(kept)!.TotalAllTokens.Should().Be(600);
        restarted.GetTotalTokens().Should().Be(750);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
