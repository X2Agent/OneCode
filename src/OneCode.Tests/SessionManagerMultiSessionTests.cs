using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using NSubstitute;

namespace OneCode.Tests;

public sealed class SessionManagerMultiSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _store;
    private readonly SessionManager _manager;

    public SessionManagerMultiSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionMgrTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SessionStore(basePath: _tempDir, NullLogger<SessionStore>.Instance);
        _manager = new SessionManager(_store, NullLogger<SessionManager>.Instance, _tempDir,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());
    }

    [Fact]
    public async Task EnsureActiveSessionAsync_ReusesExistingSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);
        var second = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-b"), ct);

        second.Id.Should().Be(first.Id);
        second.Model.Should().Be("model-b");
    }

    [Fact]
    public async Task AppendMessages_PersistsTranscript()
    {
        var ct = TestContext.Current.CancellationToken;
        await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        await _manager.AppendUserMessageAsync("hello", ct);
        await _manager.AppendAssistantMessageAsync("world", new TokenUsage(10, 5), ct);

        var reloaded = await _store.LoadAsync(_manager.ForegroundConversation!.Id, ct);
        reloaded.Should().NotBeNull();
        reloaded!.Messages.Should().HaveCount(2);
        reloaded.Messages[0].Should().BeOfType<UserMessage>();
        reloaded.Messages[1].Should().BeOfType<AssistantMessage>();
    }

    [Fact]
    public async Task AppendCompletedToolBatchesAsync_ReplayAndRestart_PersistsBatchExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        var batch = CreateCompletedBatch("run-restart:1", "run-restart", "call-restart");

        await Task.WhenAll(
            _manager.AppendCompletedToolBatchesAsync(conversation.Id, [batch], ct),
            _manager.AppendCompletedToolBatchesAsync(conversation.Id, [batch], ct));
        await _manager.AppendCompletedToolBatchesAsync(conversation.Id, [batch], ct);

        var firstReload = await _store.LoadAsync(conversation.Id, ct);
        firstReload.Should().NotBeNull();
        firstReload!.Messages.Should().HaveCount(2);
        firstReload.Messages.OfType<AssistantMessage>().Should().ContainSingle()
            .Which.Id.Should().Be("tool-batch:run-restart:1");
        firstReload.Messages.OfType<ToolResultMessage>().Should().ContainSingle();

        var restarted = CreateManager();
        await restarted.ResumeAsync(conversation.Id.ToString(), ct);
        await restarted.AppendCompletedToolBatchesAsync(conversation.Id, [batch], ct);

        var secondReload = await _store.LoadAsync(conversation.Id, ct);
        secondReload.Should().NotBeNull();
        secondReload!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendCompletedToolBatchesAsync_IncompleteBatch_RejectsWithoutPersisting()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        var incomplete = new CompletedToolBatch(
            "run-open:1",
            "run-open",
            [new CompletedToolCallRecord("call-open", "Read", "{}", 0)],
            [],
            DateTimeOffset.UtcNow);

        var act = () => _manager.AppendCompletedToolBatchesAsync(conversation.Id, [incomplete], ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incomplete*");
        // Deferred persistence: the empty conversation was never written to disk.
        var reloaded = await _store.LoadAsync(conversation.Id, ct);
        reloaded.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_EmptyConversation_DoesNotWriteSessionFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);

        (await _store.LoadAsync(conversation.Id, ct)).Should().BeNull();
        (await _manager.ListAsync(ct)).Should().NotContain(s => s.Id == conversation.Id);
    }

    [Fact]
    public async Task AppendUserMessageAsync_FirstMessage_PersistsSessionFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);

        await _manager.AppendUserMessageAsync("first real message", ct);

        var reloaded = await _store.LoadAsync(conversation.Id, ct);
        reloaded.Should().NotBeNull();
        reloaded!.Messages.OfType<UserMessage>()
            .Should().ContainSingle(um => um.Content == "first real message");
    }

    [Fact]
    public async Task SetForegroundMode_BeforeFirstSave_PersistsModeWithFirstMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        _manager.SetForegroundMode("plan");

        await _manager.AppendUserMessageAsync("plan this", ct);

        var reloaded = await _store.LoadAsync(conversation.Id, ct);
        reloaded!.Metadata["mode"].ToString().Should().Be("plan");
        (await _manager.ListAsync(ct))
            .Single(s => s.Id == conversation.Id).Mode.Should().Be("plan");
    }

    [Fact]
    public async Task BackgroundCurrentAndCreateNewAsync_KeepsPreviousInBackground()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        await _manager.AppendUserMessageAsync("running task", ct);

        var second = await _manager.BackgroundCurrentAndCreateNewAsync(new ConversationOptions(_tempDir), ct);

        second.Id.Should().NotBe(first.Id);
        _manager.BackgroundSessionCount.Should().Be(1);
        _manager.ForegroundConversation!.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task SwitchToSessionAsync_RestoresBackgroundSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        await _manager.BackgroundCurrentAndCreateNewAsync(new ConversationOptions(_tempDir), ct);

        var restored = await _manager.SwitchToSessionAsync(first.Id.Value, ct);
        restored.Should().NotBeNull();
        restored!.Id.Should().Be(first.Id);
        _manager.BackgroundSessionCount.Should().Be(1);
    }

    private SessionManager CreateManager() => new(
        _store,
        NullLogger<SessionManager>.Instance,
        _tempDir,
        hookExecutionService: Substitute.For<IHookExecutionService>(),
        shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
        tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
        sessionIdHolder: new SessionIdHolder(),
        sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());

    private static CompletedToolBatch CreateCompletedBatch(
        string batchId,
        string runId,
        string callId) => new(
        batchId,
        runId,
        [new CompletedToolCallRecord(callId, "Read", "{}", 0)],
        [new CompletedToolResultRecord(
            callId,
            "Read",
            "ok",
            IsError: false,
            ToolResultCompletion.Succeeded,
            Order: 0)],
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SessionManagerMultiSessionTests cleanup failed: {ex.Message}"); }
    }
}
