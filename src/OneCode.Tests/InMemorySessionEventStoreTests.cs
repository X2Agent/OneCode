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

        var stamp = await store.AppendAsync([
            new SessionMarkerEvent(sessionId, 99, now, SessionEventType.TurnStarted),
            new SessionMarkerEvent(sessionId, 1, now, SessionEventType.TurnEnded),
        ]);

        stamp.Should().NotBeNull("写入必须回传日志戳供摘要索引回填");
        stamp.GetValueOrDefault().Length.Should().Be(2);
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

        var stamps = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => store.AppendAsync([
                new SessionMarkerEvent(sessionId, 0, now, SessionEventType.StepStarted),
            ])));

        // 每次追加 1 条事件：内存实现的 Length 即累计条数，必然覆盖 1..32 且互不重复。
        var lengths = stamps.Select(stamp => stamp.GetValueOrDefault().Length).ToArray();
        lengths.Should().OnlyHaveUniqueItems();
        lengths.OrderBy(length => length)
            .Should().Equal(Enumerable.Range(1, 32).Select(value => (long)value));
    }
}
