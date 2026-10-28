using OneCode.Infrastructure.Persistence;
using OneCode.Core.Session;

namespace OneCode.Tests;

public sealed class FileSessionEventStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-events-" + Guid.NewGuid().ToString("N"));

    public FileSessionEventStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AppendAndRead_RoundTripsTypedEvents()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        var stamp = await store.AppendAsync([
            new UserMessageEvent(sessionId, 99, now, new UserMessage("u", "hello", now)),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.TurnEnded, "done"),
        ]);

        var events = await store.ReadAsync(sessionId);

        stamp.Should().NotBeNull("写入必须回传日志戳供摘要索引回填");
        var written = stamp.GetValueOrDefault();
        written.SessionId.Should().Be(sessionId);
        written.Length.Should().BePositive();
        events.Select(item => item.Sequence).Should().Equal(1, 2);
        events.Should().HaveCount(2);
        events[0].Should().BeOfType<UserMessageEvent>();
        events[1].Should().BeOfType<SessionMarkerEvent>();
        ((SessionMarkerEvent)events[1]).Data.Should().Be("done");
    }

    [Fact]
    public async Task Append_ConcurrentBatches_ProducesContinuousSequences()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.AppendAsync([
            new SessionMarkerEvent(sessionId, index, now, SessionEventType.StepStarted),
            new SessionMarkerEvent(sessionId, index, now, SessionEventType.StepEnded),
        ])));

        var events = await store.ReadAsync(sessionId);
        events.Select(item => item.Sequence).Should().Equal(Enumerable.Range(1, 16).Select(value => (long)value));
    }

    /// <summary>
    /// 跨进程一致性契约：两个 store 实例 = 两个进程（进程内锁互不相见、共享同一磁盘）。
    /// 交错追加不得丢事件、Sequence 必须连续——无跨进程锁时后写者的
    /// 「读旧 → 整文件覆盖」会先写者整批丢失且 Sequence 重复。
    /// </summary>
    [Fact]
    public async Task Append_TwoStoreInstancesInterleaved_NoLostEventsAndContinuousSequences()
    {
        var storeA = new FileSessionEventStore(_root);
        var storeB = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            (index % 2 == 0 ? storeA : storeB).AppendAsync([
                new SessionMarkerEvent(sessionId, index, now, SessionEventType.StepStarted),
            ])));

        var events = await storeA.ReadAsync(sessionId);
        events.Should().HaveCount(8, "双进程交错写入不得丢事件");
        events.Select(item => item.Sequence).Should().Equal(Enumerable.Range(1, 8).Select(value => (long)value));
    }

    /// <summary>锁文件 DeleteOnClose 契约：写完后不得残留 .lock 文件阻塞后续进程。</summary>
    [Fact]
    public async Task Append_Completes_LockFileNotLeftBehind()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, DateTimeOffset.UtcNow, SessionEventType.StepStarted),
        ]);

        Directory.EnumerateFiles(_root, "*.lock", SearchOption.AllDirectories)
            .Should().BeEmpty("DeleteOnClose 锁在释放后必须消失");
    }

    /// <summary>
    /// 真追加契约：已落盘字节不被改写。旧实现每次追加整文件复制覆盖——文件越大写放大越大；
    /// 回退到旧实现本测试不会失败（内容等价），故以「前缀字节逐一不变」锚定追加语义。
    /// </summary>
    [Fact]
    public async Task Append_PreservesExistingBytesInPlace_ContinuesSequenceFromTail()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepStarted, "first"),
        ]);
        var file = Path.Combine(_root, ".onecode", "events", sessionId.Value + ".jsonl");
        var bytesBefore = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepEnded, "second"),
        ]);

        var bytesAfter = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        bytesAfter.Length.Should().BeGreaterThan(bytesBefore.Length);
        bytesAfter.AsSpan(0, bytesBefore.Length).SequenceEqual(bytesBefore)
            .Should().BeTrue("追加只允许在尾部追加，既有事件字节必须原样保留");

        var events = await store.ReadAsync(sessionId);
        events.Select(item => item.Sequence).Should().Equal(1L, 2L);
        ((SessionMarkerEvent)events[1]).Data.Should().Be("second");
    }

    /// <summary>中段坏行容错：读取跳过坏行，其余事件照常可读（此前一条坏行废掉整个会话）。</summary>
    [Fact]
    public async Task Read_MidFileCorruptedLine_SkipsItAndReturnsOtherEvents()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepStarted, "one"),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepEnded, "two"),
        ]);
        var file = Path.Combine(_root, ".onecode", "events", sessionId.Value + ".jsonl");
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(file,
            [lines[0], "{ this is not valid json", lines[1]],
            TestContext.Current.CancellationToken);

        var events = await store.ReadAsync(sessionId);

        events.Should().HaveCount(2, "中段坏行必须被跳过，不得让整个会话不可读");
        events.Select(item => item.Sequence).Should().Equal(1L, 2L);
    }

    /// <summary>崩溃残留半行：追加前截断丢弃；其后序列号从最后完整行继续。</summary>
    [Fact]
    public async Task Append_CorruptedPartialTail_TruncatesAndContinuesSequence()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepStarted, "one"),
        ]);
        var file = Path.Combine(_root, ".onecode", "events", sessionId.Value + ".jsonl");
        // 模拟崩溃：写入半行无换行
        await File.AppendAllTextAsync(file, """{"schema_version":1,"se""", TestContext.Current.CancellationToken);

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepEnded, "two"),
        ]);

        var events = await store.ReadAsync(sessionId);
        events.Should().HaveCount(2);
        events.Select(item => item.Sequence).Should().Equal(1L, 2L);
        ((SessionMarkerEvent)events[1]).Data.Should().Be("two");
    }

    /// <summary>完整事件缺收尾换行（flush 成功但换行未落盘）：补换行修复，不丢该事件。</summary>
    [Fact]
    public async Task Append_CompleteEventMissingTrailingNewline_RepairedAndKept()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepStarted, "solo"),
        ]);
        var file = Path.Combine(_root, ".onecode", "events", sessionId.Value + ".jsonl");
        // 模拟「事件完整落盘、收尾换行丢失」：剥离最后的换行字节
        var bytes = await File.ReadAllBytesAsync(file, ct);
        await File.WriteAllBytesAsync(file, bytes[..^1], ct);

        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepEnded, "next"),
        ]);

        var events = await store.ReadAsync(sessionId);
        events.Should().HaveCount(2, "缺换行的完整事件必须被修复保留");
        events.Select(item => ((SessionMarkerEvent)item).Data).Should().Equal("solo", "next");
        events.Select(item => item.Sequence).Should().Equal(1L, 2L);
    }

    /// <summary>RewriteAsync（快照压缩落盘）：序列号重排 1..N，读回投影等价。</summary>
    [Fact]
    public async Task Rewrite_RenumbersSequenceAndAtomicallyReplaces()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        var appended = await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepStarted, "one"),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.StepEnded, "two"),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.TurnEnded, "three"),
        ]);

        var stamp = await store.RewriteAsync(sessionId, [
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepStarted, "compact-1"),
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepEnded, "compact-2"),
        ], appended!.Value);

        stamp.Should().NotBeNull();
        var events = await store.ReadAsync(sessionId);
        events.Select(item => item.Sequence).Should().Equal(1L, 2L);
        events.Select(item => ((SessionMarkerEvent)item).Data)
            .Should().Equal("compact-1", "compact-2");
    }

    [Fact]
    public async Task Rewrite_ConcurrentAppendAfterExpectedStamp_PreservesNewEvent()
    {
        var store = new FileSessionEventStore(_root);
        var otherStore = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var appended = await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepStarted, "original"),
        ]);
        await otherStore.AppendAsync([
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepEnded, "concurrent"),
        ]);

        var rewritten = await store.RewriteAsync(sessionId, [
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.TurnEnded, "stale-compaction"),
        ], appended!.Value);

        rewritten.Should().BeNull("旧快照不得覆盖压缩期间新追加的事件");
        var events = await store.ReadAsync(sessionId);
        events.Select(item => ((SessionMarkerEvent)item).Data).Should().Equal("original", "concurrent");
    }

    [Fact]
    public async Task Append_AfterSessionDelete_RejectsLateWriter()
    {
        var store = new FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepStarted, "before-delete"),
        ]);
        await store.DeleteAsync(sessionId);

        var append = async () => await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepEnded, "late-write"),
        ]);

        await append.Should().ThrowAsync<InvalidOperationException>().WithMessage("*deleted*");
        (await store.ReadAsync(sessionId)).Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
