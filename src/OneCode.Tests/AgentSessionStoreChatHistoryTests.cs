using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>W5-A: Session transcript owns multi-turn history — InMemory must be clearable.</summary>
public sealed class AgentSessionStoreChatHistoryTests
{
    [Fact]
    public async Task ClearInMemoryChatHistoryWhenTranscriptOwnsHistory_EmptiesPriorTurns()
    {
        var store = new AgentSessionStore(
            sessionManager: null!,
            NullLogger<AgentSessionStore>.Instance);
        var chatClient = Substitute.For<IChatClient>();
        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);
        var agent = new HarnessAgent(chatClient, options);
        var session = await agent.CreateSessionAsync();

        AgentSessionExtensions.SetInMemoryChatHistory(
            session,
            [
                new ChatMessage(ChatRole.User, "earlier user"),
                new ChatMessage(ChatRole.Assistant, "earlier assistant"),
            ]);

        AgentSessionExtensions.TryGetInMemoryChatHistory(session, out var before).Should().BeTrue();
        before.Should().HaveCount(2);

        store.ClearInMemoryChatHistoryWhenTranscriptOwnsHistory(session);

        AgentSessionExtensions.TryGetInMemoryChatHistory(session, out var after).Should().BeTrue();
        after.Should().BeEmpty();
    }

    [Fact]
    public void ClearInMemoryChatHistoryWhenTranscriptOwnsHistory_NullSession_Throws()
    {
        var store = new AgentSessionStore(
            sessionManager: null!,
            NullLogger<AgentSessionStore>.Instance);
        var act = () => store.ClearInMemoryChatHistoryWhenTranscriptOwnsHistory(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}