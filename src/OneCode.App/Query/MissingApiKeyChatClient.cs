using Microsoft.Extensions.AI;
namespace OneCode.App.Query;

/// <summary>
/// Sentinel IChatClient — registered when no API key is configured.
/// Throws on first use to prompt the user to configure credentials.
/// </summary>
internal sealed class MissingApiKeyChatClient(ILogger<MissingApiKeyChatClient> _logger) : IChatClient
{
    public void Dispose() { }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogError("No API key configured. Set the ONECODE_API_KEY environment variable.");
        throw new InvalidOperationException(
            "No LLM provider configured. Set the ONECODE_API_KEY environment variable.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogError("No API key configured. Set the ONECODE_API_KEY environment variable.");
        throw new InvalidOperationException(
            "No LLM provider configured. Set the ONECODE_API_KEY environment variable.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
