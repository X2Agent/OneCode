using Microsoft.Agents.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Single entry for assembling <see cref="AIContextProvider"/> lists (W1).
/// Shared providers for all profiles; Main extras are mode-gated via
/// <see cref="MainModeContextProviderBuilder"/>.
/// </summary>
public sealed class AgentContextPipeline(
    SharedContextProviderBuilder shared,
    MainModeContextProviderBuilder main)
{
    /// <summary>Shared providers only (Worker / Explore / Plan / TeamMember).</summary>
    public List<AIContextProvider> BuildShared(
        PipelineProfile profile,
        AgentContextProviderOptions options)
        => shared.BuildCommon(profile, options);

    /// <summary>Full Main path: shared + WorkingMode-specific providers.</summary>
    public Task<List<AIContextProvider>> BuildForMainAsync(
        AgentContextProviderOptions baseOptions,
        WorkingMode workingMode,
        CancellationToken ct = default)
        => main.BuildForMainAsync(baseOptions, workingMode, ct);
}
