using Microsoft.Extensions.AI;

namespace OneCode.Core.Tools;

/// <summary>
/// Parent-query parameters that are safe to share with forked/sub agents for
/// prompt-cache alignment (system prompt, model, tools snapshot).
/// </summary>
public sealed class CacheSafeParams
{
    public string? SystemPrompt { get; init; }
    public string? ModelId { get; init; }
    public int? ThinkingBudget { get; init; }
    public IReadOnlyList<AITool>? Tools { get; init; }
    public ToolCapabilitySet? ToolCapabilities { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Provides the latest <see cref="CacheSafeParams"/> snapshot from the parent conversation.
/// Null until the first query completes.
/// </summary>
public interface ICacheSafeParamsProvider
{
    CacheSafeParams? Current { get; }
}
