using OneCode.Core.Domain;
using OneCode.Core.Session;

namespace OneCode.Tests;

public sealed class FileSessionEventStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-events-" + Guid.NewGuid().ToString("N"));

    public FileSessionEventStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AppendAndRead_RoundTripsTypedEvents()
    {
        var store = new OneCode.App.Session.FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        var sequences = await store.AppendAsync([
            new UserMessageEvent(sessionId, 99, now, new UserMessage("u", "hello", now)),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.TurnEnded, "done"),
        ]);

        var events = await store.ReadAsync(sessionId);

        sequences.Should().Equal(1, 2);
        events.Should().HaveCount(2);
        events[0].Should().BeOfType<UserMessageEvent>();
        events[1].Should().BeOfType<SessionMarkerEvent>();
        ((SessionMarkerEvent)events[1]).Data.Should().Be("done");
    }

    [Fact]
    public async Task Append_ConcurrentBatches_ProducesContinuousSequences()
    {
        var store = new OneCode.App.Session.FileSessionEventStore(_root);
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.AppendAsync([
            new SessionMarkerEvent(sessionId, index, now, SessionEventType.StepStarted),
            new SessionMarkerEvent(sessionId, index, now, SessionEventType.StepEnded),
        ])));

        var events = await store.ReadAsync(sessionId);
        events.Select(item => item.Sequence).Should().Equal(Enumerable.Range(1, 16).Select(value => (long)value));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}