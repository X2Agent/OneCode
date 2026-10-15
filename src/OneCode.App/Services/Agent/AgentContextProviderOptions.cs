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

    /// <summary>
    /// Working mode of the run that owns these providers. Providers whose surface contradicts the
    /// mode's capability boundary (e.g. CodeAct in Plan mode) are skipped at assembly time, so the
    /// mode restriction does not rely solely on the permission layer rejecting calls afterwards.
    /// </summary>
    public WorkingMode? WorkingMode { get; init; }

    /// <summary>
    /// True when the run has no approval channel (controlled Build: tool permissions were confirmed
    /// at the plan gate and the interactive broker is suppressed). Providers whose tools force an
    /// approval boundary regardless of the product's approval gating (CodeAct <c>execute_code</c>
    /// is AlwaysRequire at the framework level) must not be mounted — the resulting approval
    /// request has no one to answer it and fails the whole attempt.
    /// </summary>
    public bool SuppressToolApproval { get; init; }
}
