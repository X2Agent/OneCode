using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Applies the product summary output limit when the caller did not request one.
/// </summary>
/// <remarks>
/// <para>
/// MAF's summarisation strategy invokes the chat client without a <see cref="ChatOptions"/>, so the
/// response length falls back to the provider default. The explicit <c>/compact</c> path passes its
/// own limit, which made the two paths produce differently sized summaries for the same conversation.
/// Wrapping the summarisation client normalises the bound without touching either call site.
/// </para>
/// <para>
/// An explicit <see cref="ChatOptions.MaxOutputTokens"/> always wins: this only fills in a missing
/// value, it does not cap a deliberate one.
/// </para>
/// <para>
/// Derives from <see cref="DelegatingChatClient"/> rather than hand-implementing <see cref="IChatClient"/>:
/// the base supplies <c>GetService</c>, <c>Dispose</c> and the streaming default, so only the two
/// interception points appear here. Hand-written pass-through members are how decorators silently lose
/// capabilities — a forgotten <c>GetService</c> hides the provider's own services from callers.
/// </para>
/// </remarks>
internal sealed class SummarizationOutputLimitChatClient(IChatClient inner, int maxOutputTokens)
    : DelegatingChatClient(inner)
{
    private readonly int _maxOutputTokens = maxOutputTokens > 0
        ? maxOutputTokens
        : throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), maxOutputTokens, "Must be positive.");

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, WithDefaultLimit(options), cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, WithDefaultLimit(options), cancellationToken);

    /// <summary>
    /// Fills in the product limit when the caller supplied none, cloning rather than mutating —
    /// the caller may reuse its <see cref="ChatOptions"/> instance.
    /// </summary>
    private ChatOptions WithDefaultLimit(ChatOptions? options)
    {
        if (options?.MaxOutputTokens is > 0)
            return options;

        var filled = options?.Clone() ?? new ChatOptions();
        filled.MaxOutputTokens = _maxOutputTokens;
        return filled;
    }
}
