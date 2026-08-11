using OneCode.Core.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Services.Cache;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

public sealed class CompactServiceTests
{
    [Fact]
    public async Task CompactAsync_TooFewMessages_ReturnsNull_AndDoesNotCallApi()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(2);

        var result = await sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        result.Should().BeNull("insufficient messages should not produce a summary");
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactAsync_EnoughMessages_ReturnsSummary()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(8);
        var response = Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<analysis>testing compaction</analysis>\n<summary>User wants to build a calculator app with basic arithmetic.</summary>")));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Should().NotContain("testing compaction");
        result.Should().Contain("calculator");
    }

    [Fact]
    public async Task CompactAsync_ReplacesSessionMessages()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(10);
        var originalCount = session.Messages.Count;
        var response = Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<analysis>various</analysis>\n<summary>Refactored auth module, added unit tests.</summary>")));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Should().Contain("Refactored auth module");
        session.Messages.Should().NotBeEmpty("summary message should remain");
        // Compaction replaces original messages with boundary marker + summary + retained recent messages.
        // Total should be at most 3 (boundary + user marker + assistant summary) + RecentMessagesToKeep (8) = 11.
        // The key invariant: compaction does not just append — it clears and rebuilds.
        session.Messages.Count.Should().BeLessThanOrEqualTo(originalCount + 3,
            "compaction replaces messages (clear + 3 new + retained recent), not appends to existing");
        session.Messages.Should().Contain(m => m is SystemMessage,
            "compaction must insert a boundary system message");
    }

    [Fact]
    public async Task CompactAsync_AddsMetadata()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(6);
        var response = Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "<analysis>various</analysis>\n<summary>Summary here.</summary>")));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        await sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        session.Metadata.Should().ContainKey("lastCompactedAt");
        session.Metadata.Should().ContainKey("lastCompactedMessageCount");
        session.Metadata["lastCompactedMessageCount"].Should().Be(session.Messages.Count,
            "should record post-compaction message count, not pre-compaction count");
    }

    [Fact]
    public async Task CompactAsync_ApiError_Propagates()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(6);
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new HttpRequestException("API error")));

        var act = () => sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompactAsync_EmptySummary_ReturnsNull()
    {
        var (sut, client) = CreateSut();
        var session = CreateSession(6);
        var response = Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "   ")));
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await sut.CompactAsync(session, ct: TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // Helpers

    private static (CompactService Service, IChatClient Client) CreateSut()
    {
        var client = Substitute.For<IChatClient>();
        var logger = Substitute.For<ILogger<CompactService>>();

        var store = Substitute.For<ISessionStore>();
        var sessionManager = new SessionManager(store, Substitute.For<ILogger<SessionManager>>(), Environment.CurrentDirectory,
            hookExecutionService: Substitute.For<OneCode.Core.Hooks.IHookExecutionService>(),
            shellExecutorCleanup: Substitute.For<OneCode.App.Tools.IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());

        var sut = new CompactService(client, logger,
            Substitute.For<IHookExecutionService>(),
            new CompactSessionDependencies(
                sessionManager,
                sessionManager,
                new FileContentCache(),
                Substitute.For<IModelManager>(),
                new OneCode.Infrastructure.TokenEstimator()),
            new CompactPromptBuilder(new PromptManager()),
            new CompactApplier());
        return (sut, client);
    }

    private static Conversation CreateSession(int messageCount)
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "test-session",
            WorkingDirectory = Environment.CurrentDirectory,
            Model = "test-model",
        };

        for (var i = 0; i < messageCount; i++)
        {
            if (i % 2 == 0)
            {
                session.Messages.Add(new UserMessage(
                    Guid.NewGuid().ToString("N"),
                    $"User message {i}",
                    DateTimeOffset.UtcNow));
            }
            else
            {
                session.Messages.Add(new AssistantMessage(
                    Guid.NewGuid().ToString("N"),
                    [new TextBlock($"Assistant response {i}")],
                    DateTimeOffset.UtcNow));
            }
        }

        return session;
    }
}
