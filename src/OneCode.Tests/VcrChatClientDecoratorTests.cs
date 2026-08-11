using Microsoft.Extensions.AI;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Ai;
using System.Runtime.CompilerServices;

namespace OneCode.Tests;

public sealed class VcrChatClientDecoratorTests : IDisposable
{
    private readonly string _fixtureDir;

    public VcrChatClientDecoratorTests()
    {
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"onecode-vcr-chat-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_fixtureDir))
                Directory.Delete(_fixtureDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; temp files are harmless.
        }
    }

    [Fact]
    public async Task GetResponseAsync_Inactive_PassesThroughToInner()
    {
        var vcr = CreateVcrMode(isActive: false, isRecording: false);
        var inner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "real")));
        var sut = new VcrChatClientDecorator(inner, vcr, _fixtureDir);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.Should().Be("real");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetResponseAsync_Recording_SavesFixtureAndReturnsRealResponse()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recorded"))
        {
            ResponseId = "resp-1",
            ModelId = "test-model",
        });
        var sut = new VcrChatClientDecorator(inner, vcr, _fixtureDir);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.Should().Be("recorded");
        response.ResponseId.Should().Be("resp-1");
        response.ModelId.Should().Be("test-model");
        inner.CallCount.Should().Be(1);
        Directory.EnumerateFiles(_fixtureDir, "*.json").Should().ContainSingle();
    }

    [Fact]
    public async Task GetResponseAsync_Replay_ReturnsCachedWithoutCallingInner()
    {
        // Record first
        var recordVcr = CreateVcrMode(isActive: true, isRecording: true);
        var recordInner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "cached")));
        var recordSut = new VcrChatClientDecorator(recordInner, recordVcr, _fixtureDir);
        await recordSut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        // Replay with a fresh inner that would return a different response
        var replayVcr = CreateVcrMode(isActive: true, isRecording: false);
        var replayInner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "should-not-use")));
        var replaySut = new VcrChatClientDecorator(replayInner, replayVcr, _fixtureDir);

        var response = await replaySut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.Should().Be("cached");
        replayInner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Recording_CollectsAndSavesUpdates()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new FakeChatClient(streamingUpdates:
        [
            new ChatResponseUpdate(ChatRole.Assistant, "Hello"),
            new ChatResponseUpdate(ChatRole.Assistant, " world"),
        ]);
        var sut = new VcrChatClientDecorator(inner, vcr, _fixtureDir);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(u);

        updates.Should().HaveCount(2);
        updates[0].Text.Should().Be("Hello");
        updates[1].Text.Should().Be(" world");
        Directory.EnumerateFiles(_fixtureDir, "*.stream.json").Should().ContainSingle();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Replay_ReturnsCachedWithoutCallingInner()
    {
        // Record
        var recordVcr = CreateVcrMode(isActive: true, isRecording: true);
        var recordInner = new FakeChatClient(streamingUpdates: [new ChatResponseUpdate(ChatRole.Assistant, "streamed")]);
        var recordSut = new VcrChatClientDecorator(recordInner, recordVcr, _fixtureDir);
        await foreach (var _ in recordSut.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)) { }

        // Replay
        var replayVcr = CreateVcrMode(isActive: true, isRecording: false);
        var replayInner = new FakeChatClient(streamingUpdates: [new ChatResponseUpdate(ChatRole.Assistant, "should-not-use")]);
        var replaySut = new VcrChatClientDecorator(replayInner, replayVcr, _fixtureDir);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in replaySut.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(u);

        updates.Should().HaveCount(1);
        updates[0].Text.Should().Be("streamed");
        replayInner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DifferentMessages_ProduceDifferentFixtures()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "resp")));
        var sut = new VcrChatClientDecorator(inner, vcr, _fixtureDir);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "first")], cancellationToken: TestContext.Current.CancellationToken);
        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "second")], cancellationToken: TestContext.Current.CancellationToken);

        Directory.EnumerateFiles(_fixtureDir, "*.json").Should().HaveCount(2);
    }

    private static VcrMode CreateVcrMode(bool isActive, bool isRecording) =>
        isActive switch
        {
            false => VcrMode.Inactive,
            true when isRecording => VcrMode.Record,
            _ => VcrMode.Replay,
        };

    private sealed class FakeChatClient : IChatClient
    {
        private readonly ChatResponse? _nonStreamingResponse;
        private readonly ChatResponseUpdate[]? _streamingUpdates;
        private int _callCount;

        public FakeChatClient(ChatResponse nonStreamingResponse) => _nonStreamingResponse = nonStreamingResponse;
        public FakeChatClient(ChatResponseUpdate[] streamingUpdates) => _streamingUpdates = streamingUpdates;

        public int CallCount => _callCount;

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_nonStreamingResponse!);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            foreach (var u in _streamingUpdates!)
                yield return u;
        }
    }
}
