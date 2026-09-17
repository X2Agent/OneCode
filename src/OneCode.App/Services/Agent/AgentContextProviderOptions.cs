namespace OneCode.App.Services.Agent;

/// <summary>
/// Per-run inputs for <see cref="SharedContextProviderBuilder.BuildCommon"/>.
/// </summary>
/// <remarks>
/// Which providers are included is a function of the <c>PipelineProfile</c> (see
/// <c>PipelineProfileBehavior</c> and <c>AgentCapability</c>), not a per-call flag set. This record
/// therefore carries only run-scoped values — the same profile with different working directories or
/// conversations still yields the same capability set.
/// </remarks>
public sealed record AgentContextProviderOptions
{
    public required string WorkingDirectory { get; init; }
    public SessionId? ConversationId { get; init; }
}
