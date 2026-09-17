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
        var events = new FileSessionEventStore(_root);
        var store = new EventSourcedSessionStore(events);
        var conversation = CreateConversation();
        conversation.Messages.Add(new UserMessage("user", "first", DateTimeOffset.UtcNow));

        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);
        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);
        conversation.Messages.Add(new UserMessage("user-2", "second", DateTimeOffset.UtcNow));
        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
        loaded!.Messages.Select(message => ((UserMessage)message).Content)
            .Should().Equal("first", "second");
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