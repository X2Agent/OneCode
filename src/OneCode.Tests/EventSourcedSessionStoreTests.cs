using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Infrastructure.Persistence;
using OneCode.App.Session;
using OneCode.Core.Session;

namespace OneCode.Tests;

public sealed class EventSourcedSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-event-store-" + Guid.NewGuid().ToString("N"));

    public EventSourcedSessionStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SaveAndLoad_RebuildsConversationFromEventLog()
    {
        var events = new FileSessionEventStore(_root);
        var store = CreateStore(events);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("user", "hello", DateTimeOffset.UtcNow));

        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be(conversation.Name);
        loaded.Model.Should().Be(conversation.Model);
        loaded.Messages.Should().ContainSingle().Which.Should().BeOfType<UserMessage>()
            .Which.Content.Should().Be("hello");
    }

    [Fact]
    public async Task SaveAsync_AppendsOnlyNewMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new FileSessionEventStore(_root);
        var store = CreateStore(events);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("user", "first", DateTimeOffset.UtcNow));

        await store.SaveAsync(conversation, ct);
        await store.SaveAsync(conversation, ct);
        conversation.Messages.Add(new AssistantMessage(
            "asst-1",
            [new TextBlock("reply 1"), new ToolUseBlock("call-1", "Read", """{"path":"a.cs"}""")],
            DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, ct);
        conversation.Messages.Add(new ToolResultMessage(
            "tool-1", "call-1", "Read", "file content", IsError: false, DateTimeOffset.UtcNow));
        conversation.Messages.Add(new UserMessage("user-2", "second", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, ct);

        var loaded = await store.LoadAsync(conversation.Id, ct);
        loaded!.Messages.Select(message => message.Id)
            .Should().Equal("user", "asst-1", "tool-1", "user-2");

        var storedEvents = await events.ReadAsync(conversation.Id, ct);
        storedEvents.OfType<MessagesReplacedEvent>().Should().BeEmpty(
            "normal message appends must emit incremental events without full-history MessagesReplacedEvent");
        storedEvents.OfType<UserMessageEvent>().Should().HaveCount(2);
        storedEvents.OfType<AssistantMessageEvent>().Should().ContainSingle();
        storedEvents.OfType<ToolResultEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task SaveAsync_AfterMessageReplacement_RebuildsOnlyCurrentMessages()
    {
        var events = new FileSessionEventStore(_root);
        var store = CreateStore(events);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("old", "before compact", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

        conversation.Messages.Clear();
        conversation.Messages.Add(new UserMessage("new", "after compact", DateTimeOffset.UtcNow));
        conversation.Metadata["lastMafSessionInvalidationSource"] = "compact.full";
        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
        loaded!.Messages.Should().ContainSingle().Which.Should().BeOfType<UserMessage>()
            .Which.Content.Should().Be("after compact");
        var replacements = (await events.ReadAsync(conversation.Id, TestContext.Current.CancellationToken))
            .OfType<MessagesReplacedEvent>()
            .ToArray();
        replacements.Should().HaveCount(1);
        replacements[0].Reason.Should().Be("compact.full");
    }

    [Fact]
    public async Task ListAsync_Title_PrefersNameElseFirstUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new FileSessionEventStore(_root);
        var store = CreateStore(events);

        var named = CreateConversation();
        named.Messages.Add(new UserMessage("u1", "first message", DateTimeOffset.UtcNow));
        await store.SaveAsync(named, ct);

        var unnamed = CreateConversation();
        unnamed.Name = "";
        unnamed.Messages.Add(new UserMessage("u2", "帮我修复登录超时", DateTimeOffset.UtcNow));
        await store.SaveAsync(unnamed, ct);

        var summaries = await store.ListAsync(ct);

        summaries.Single(s => s.Id == named.Id).Title.Should().Be("event-sourced");
        summaries.Single(s => s.Id == unnamed.Id).Title.Should().Be("帮我修复登录超时");
    }

    [Fact]
    public async Task ListAsync_AfterSave_ServesSummaryFromCacheWithoutReadingLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var counting = new CountingSessionEventStore(new FileSessionEventStore(_root));
        var store = CreateStore(counting);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, ct);

        counting.ResetReadCount();
        var first = await store.ListAsync(ct);
        var second = await store.ListAsync(ct);

        first.Should().ContainSingle().Which.MessageCount.Should().Be(1);
        second.Should().Equal(first);
        counting.ReadCount.Should().Be(0, "写入端已回填摘要，重复列表请求不得再次解析日志");
    }

    [Fact]
    public async Task ListAsync_AfterExternalAppend_RefreshesSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileStore = new FileSessionEventStore(_root);
        var store = CreateStore(fileStore);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("u1", "first", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, ct);
        (await store.ListAsync(ct)).Single().MessageCount.Should().Be(1);

        // 另一个会话持有者追加消息：日志戳变化必须让摘要缓存失效并重新解析。
        await fileStore.AppendAsync(
            [new UserMessageEvent(
                conversation.Id,
                0,
                DateTimeOffset.UtcNow,
                new UserMessage("u2", "second", DateTimeOffset.UtcNow))],
            ct);

        (await store.ListAsync(ct)).Single().MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_DropsCachedSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(new FileSessionEventStore(_root));
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, ct);
        (await store.ListAsync(ct)).Should().ContainSingle();

        await store.DeleteAsync(conversation.Id, ct);

        (await store.ListAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_InFreshStoreInstance_ServesSummaryFromDiskIndexWithoutReadingLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileStore = new FileSessionEventStore(_root);
        var seed = CreateStore(fileStore);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));
        await seed.SaveAsync(conversation, ct);

        // 模拟进程重启：内存缓存与事件存储实例都是全新的，只有落盘索引可用。
        var counting = new CountingSessionEventStore(fileStore);
        var restarted = CreateStore(counting);

        var summaries = await restarted.ListAsync(ct);

        var summary = summaries.Should().ContainSingle().Which;
        summary.MessageCount.Should().Be(1);
        summary.Title.Should().Be("event-sourced");
        counting.ReadCount.Should().Be(0, "摘要索引命中时不得解析事件日志");
    }

    [Fact]
    public async Task ListAsync_CorruptIndex_FallsBackToEventLogAndRewritesIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileStore = new FileSessionEventStore(_root);
        var seed = CreateStore(fileStore);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));
        await seed.SaveAsync(conversation, ct);

        var indexPath = Path.Combine(
            _root,
            OneCode.Infrastructure.Config.Constants.App.ConfigDirName,
            OneCode.Infrastructure.Config.Constants.Subdirs.Events,
            OneCode.Infrastructure.Config.Constants.Subdirs.SessionSummaries,
            $"{conversation.Id.Value}.json");
        File.Exists(indexPath).Should().BeTrue("保存会话时必须写入摘要索引");
        await File.WriteAllTextAsync(indexPath, "{ not json", ct);

        var counting = new CountingSessionEventStore(fileStore);
        var summary = (await CreateStore(counting).ListAsync(ct)).Should().ContainSingle().Which;

        summary.MessageCount.Should().Be(1);
        counting.ReadCount.Should().Be(1, "索引损坏必须回退解析事件日志");

        // 回退后必须回写索引：再开一个全新实例列一次，就不再读日志。
        var verifying = new CountingSessionEventStore(fileStore);
        await CreateStore(verifying).ListAsync(ct);
        verifying.ReadCount.Should().Be(0, "回退解析后必须回写索引");
    }

    private EventSourcedSessionStore CreateStore(ISessionEventStore events) =>
        new(events,
            new FileSessionSummaryIndex(_root, NullLogger<FileSessionSummaryIndex>.Instance),
            NullLogger<EventSourcedSessionStore>.Instance);

    private Conversation CreateConversation() => new()
    {
        Id = SessionId.NewId(),
        Name = "event-sourced",
        WorkingDirectory = _root,
        Model = "test-model",
        Status = ConversationStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>统计事件日志读取次数的包装：反证摘要缓存确实避免了重复解析。</summary>
    private sealed class CountingSessionEventStore(ISessionEventStore inner) : ISessionEventStore
    {
        public int ReadCount { get; private set; }

        public void ResetReadCount() => ReadCount = 0;

        public Task<SessionEventLogStamp?> AppendAsync(
            IReadOnlyList<SessionEvent> events,
            CancellationToken ct = default) => inner.AppendAsync(events, ct);

        public Task<IReadOnlyList<SessionEvent>> ReadAsync(SessionId sessionId, CancellationToken ct = default)
        {
            ReadCount++;
            return inner.ReadAsync(sessionId, ct);
        }

        public Task<IReadOnlyList<SessionEventLogStamp>> ListLogsAsync(CancellationToken ct = default) =>
            inner.ListLogsAsync(ct);

        public Task DeleteAsync(SessionId sessionId, CancellationToken ct = default) =>
            inner.DeleteAsync(sessionId, ct);
    }
}
