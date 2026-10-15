using OneCode.App.Session;
using OneCode.Core.Domain;
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
        var store = new EventSourcedSessionStore(events);
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
        var store = new EventSourcedSessionStore(events);
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
        var store = new EventSourcedSessionStore(events);
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
        var store = new EventSourcedSessionStore(events);

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
}