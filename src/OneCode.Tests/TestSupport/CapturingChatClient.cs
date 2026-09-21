using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// <see cref="IChatClient"/> test double that records the <see cref="ChatOptions"/> the model
/// actually receives, so assertions can target the final request instead of the options objects
/// the product hands to MAF. Used by the harness-instructions wiring tests and anywhere a test
/// needs the post-composition instruction text.
/// </summary>
public sealed class CapturingChatClient : IChatClient
{
    /// <summary>Instructions from the most recent request (streaming or not).</summary>
    public string? LastInstructions { get; private set; }

    /// <summary>The most recent <see cref="ChatOptions"/> seen, for tool/tool-mode assertions.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Messages from the most recent request, materialised so assertions can inspect them.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Capture(messages, options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Capture(messages, options);
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    private void Capture(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        LastOptions = options;
        LastInstructions = options?.Instructions;
        LastMessages = messages.ToList();
    }
}