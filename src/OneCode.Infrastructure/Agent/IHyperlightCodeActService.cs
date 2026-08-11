using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Creates an optional Hyperlight CodeAct sandbox <see cref="AIContextProvider"/>.
/// Returns null when the Hyperlight runtime is unavailable (silent degrade).
/// </summary>
public interface IHyperlightCodeActService
{
    /// <summary>
    /// Attempts to create a CodeAct provider for <paramref name="workingDirectory"/>.
    /// </summary>
    AIContextProvider? TryCreateProvider(
        string workingDirectory,
        IReadOnlyList<AIFunction>? sandboxTools = null);
}
