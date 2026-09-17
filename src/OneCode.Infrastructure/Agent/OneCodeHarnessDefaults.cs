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
    /// Applies OneCode product opt-outs onto an already-constructed options instance.
    /// Does not touch path-specific fields such as <see cref="HarnessAgentOptions.DisableToolAutoApproval"/>
    /// or <see cref="HarnessAgentOptions.MaximumIterationsPerRequest"/>.
    /// </summary>
    public static void ApplyProductOptOuts(HarnessAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Hard dual-mount removals — product replacements exist.
        options.DisableTodoProvider = true;
        options.DisableAgentModeProvider = true;
        options.DisableFileMemory = true;
        options.DisableAgentSkillsProvider = true;
        options.DisableWebSearch = true;

        // Defensive: compression stays on ChatClient CompactionProvider, not Harness.
        options.DisableCompaction = true;

        // Avoid stacking MAF DefaultInstructions on top of product prompts.
        options.HarnessInstructions = "";
    }
}
