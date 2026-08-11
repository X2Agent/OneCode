using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// Shared helpers for constructing lightweight <see cref="AIAgent"/> instances in tests.
/// Avoids the heavy dependency chain of <c>TeamAgentFactory</c>.
/// </summary>
internal static class TestAgents
{
    /// <summary>
    /// Creates a deterministic AIAgent that returns a fixed response message.
    /// Used by checkpoint/resume tests to verify workflow state without real LLM calls.
    /// </summary>
    public static AIAgent CreateCountingAgent(string name, string? fixedReply = null)
    {
        fixedReply ??= $"Response from {name}";
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, fixedReply));
        return new SimpleAIAgent(name, response);
    }

    /// <summary>
    /// Minimal AIAgent implementation for tests: returns a fixed <see cref="ChatResponse"/>.
    /// Session serialization uses a trivial concrete <see cref="EmptyAgentSession"/>.
    /// </summary>
    private sealed class SimpleAIAgent : AIAgent
    {
        private readonly ChatResponse _response;

        public SimpleAIAgent(string name, ChatResponse response)
        {
            // AIAgent.Id/Name/Description are read-only in this MAF version;
            // they default to the type name. For tests this is acceptable.
            _response = response;
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentResponse(_response));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            // GroupChat workflow uses RunCoreAsync, not streaming.
            // Throwing here mirrors StubAgent's behavior and is acceptable for checkpoint tests.
            throw new NotImplementedException("Streaming is not used by GroupChat workflow tests.");
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new EmptyAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, options));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement json,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new EmptyAgentSession());
    }

    /// <summary>
    /// Minimal concrete <see cref="AgentSession"/> for tests.
    /// AgentSession is abstract with a protected parameterless constructor;
    /// this subclass simply exposes it.
    /// </summary>
    private sealed class EmptyAgentSession : AgentSession
    {
        public EmptyAgentSession() : base() { }
    }
}

/// <summary>
/// Concrete stub <see cref="AIAgent"/> for testing run-level middleware.
/// Implements all abstract methods; only RunCoreAsync/RunCoreStreamingAsync are used by middleware tests.
/// </summary>
internal sealed class StubAgent : AIAgent
{
    private readonly AgentResponse? _response;
    private readonly IAsyncEnumerable<AgentResponseUpdate>? _stream;

    public StubAgent(AgentResponse response) => _response = response;

    public StubAgent(IAsyncEnumerable<AgentResponseUpdate> stream) => _stream = stream;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
        => Task.FromResult(_response!);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
        => _stream!;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken)
        => throw new NotImplementedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement json,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
