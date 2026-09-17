using OneCode.Core.Domain;
using OneCode.Core.Session;

namespace OneCode.Tests;

public sealed class SessionEventProjectionTests
{
    [Fact]
    public void DeriveMessages_SortsBySequence_AndIgnoresMarkers()
    {
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var events = new SessionEvent[]
        {
            new AssistantMessageEvent(
                sessionId,
                3,
                now,
                new AssistantMessage("assistant", [new TextBlock("answer")], now)),
            new SessionMarkerEvent(sessionId, 2, now, SessionEventType.StepEnded),
            new UserMessageEvent(
                sessionId,
                1,
                now,
                new UserMessage("user", "question", now)),
        };

        var messages = SessionEventProjection.DeriveMessages(events);

        messages.Should().HaveCount(2);
        messages[0].Should().BeOfType<UserMessage>();
        messages[1].Should().BeOfType<AssistantMessage>();
    }

    [Fact]
    public async Task SaveAsync_AfterSameIdContentEdit_ReplacesInsteadOfDropping()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-event-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new OneCode.App.Session.EventSourcedSessionStore(
                new OneCode.App.Session.FileSessionEventStore(root));
            var conversation = new Conversation
            {
                Id = SessionId.NewId(),
                Name = "edit",
                WorkingDirectory = root,
                Model = "test-model",
                Status = ConversationStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
            };
            conversation.Messages.Add(new UserMessage("user", "before", DateTimeOffset.UtcNow));
            await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

            conversation.Messages.Clear();
            conversation.Messages.Add(new UserMessage("user", "after", DateTimeOffset.UtcNow));
            await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
            loaded!.Messages.Should().ContainSingle().Which.Should().BeOfType<UserMessage>()
                .Which.Content.Should().Be("after");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}