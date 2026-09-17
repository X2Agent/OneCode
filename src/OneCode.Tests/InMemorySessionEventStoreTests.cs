using OneCode.Core.Domain;
using OneCode.Core.Session;

namespace OneCode.Tests;

public sealed class InMemorySessionEventStoreTests
{
    [Fact]
    public async Task AppendAsync_AssignsMonotonicSequencePerSession()
    {
        var store = new OneCode.App.Session.InMemorySessionEventStore();
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        var sequences = await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 99, now, SessionEventType.TurnStarted),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.TurnEnded),
        ]);

        sequences.Should().Equal(1, 2);
        var events = await store.ReadAsync(sessionId);
        events.Select(item => item.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task ReadAsync_ReturnsSessionIsolatedSnapshot()
    {
        var store = new OneCode.App.Session.InMemorySessionEventStore();
        var firstSession = SessionId.NewId();
        var secondSession = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync([
            new SessionMarkerEvent(firstSession, 0, now, SessionEventType.StepStarted),
        ]);
        await store.AppendAsync([
            new SessionMarkerEvent(secondSession, 0, now, SessionEventType.StepStarted),
        ]);

        var events = await store.ReadAsync(firstSession);

        events.Should().ContainSingle().Which.SessionId.Should().Be(firstSession);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentWriters_GetUniqueSequences()
    {
        var store = new OneCode.App.Session.InMemorySessionEventStore();
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;

        var sequences = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => store.AppendAsync([
                new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepStarted),
            ])));

        sequences.SelectMany(sequence => sequence).Should().OnlyHaveUniqueItems();
        sequences.SelectMany(sequence => sequence)
            .OrderBy(sequence => sequence)
            .Should().Equal(Enumerable.Range(1, 32).Select(value => (long)value));
    }
}