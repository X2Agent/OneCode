using Microsoft.Agents.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Shared <see cref="HarnessAgentOptions"/> product opt-outs for OneCode.
/// Keeps Main/Worker/Team/Fork and AutoDream aligned so MAF Harness defaults
/// do not dual-mount alongside product providers.
/// Boundary rationale and the replacement for each opt-out:
/// docs/adr/0007-maf-integration-boundaries.md §1.
/// </summary>
public static class OneCodeHarnessDefaults
{
    /// <summary>
    /// Explicit "no system prompt text at all" value for <see cref="HarnessAgentOptions.HarnessInstructions"/>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means "use MAF's <see cref="HarnessAgent.DefaultInstructions"/>", so paths that
    /// carry a self-contained prompt (AutoDream, Goal sub-goals) must pass this instead — otherwise MAF
    /// would add its generic instructions on top of a prompt that was never composed with them.
    /// </remarks>
    public const string SuppressFrameworkDefaults = "";

    /// <summary>
    /// Applies OneCode product opt-outs onto an already-constructed options instance.
    /// Does not touch path-specific fields such as <see cref="HarnessAgentOptions.DisableToolAutoApproval"/>
    /// or <see cref="HarnessAgentOptions.MaximumIterationsPerRequest"/>.
    /// </summary>
    public static void ApplyProductOptOuts(HarnessAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Hard dual-mount removals — product replacements exist.
        options.DisableAgentModeProvider = true;
        options.DisableAgentSkillsProvider = true;
        options.DisableWebSearch = true;

        // Todo and FileMemory are not disabled here: whether a path gets the agent's own checklist or
        // session working memory is a per-profile decision (see PipelineProfileBehavior). Disabling
        // them globally would make that decision unreachable; enabling them globally would hand write
        // tools to read-only agents.
        //
        // Harness owns compaction: the product supplies the strategy through
        // HarnessAgentOptions.CompactionStrategy and lets Harness install the provider, so there is
        // exactly one compaction path. DisableCompaction stays false (the default) — setting it here
        // would silently discard the strategy the caller provided.
        //
        // Note that compaction needs no token-parameter fallback: when CompactionStrategy is null and
        // no window/output pair is set, Harness installs no provider at all.

        // HarnessInstructions is intentionally left untouched: callers that want MAF's default
        // instructions leave it null, callers with product instructions set them, and callers that
        // want none pass <see cref="SuppressFrameworkDefaults"/>. Forcing it here would discard a
        // caller's configuration.
    }
}
