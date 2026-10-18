using Microsoft.Agents.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// OneCode 对 <see cref="HarnessAgentOptions"/> 的统一产品侧关闭项。
/// 让 Main/Worker/Fork 与 AutoDream 保持一致，避免 MAF Harness 默认实现与产品 provider 双挂载。
/// 每个关闭项都在同一 options 对象中声明了产品侧替代实现。
/// </summary>
public static class OneCodeHarnessDefaults
{
    /// <summary>
    /// <see cref="HarnessAgentOptions.HarnessInstructions"/> 的「完全不注入系统提示词」取值。
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> 表示「使用 MAF 的 <see cref="HarnessAgent.DefaultInstructions"/>」，
    /// 因此自带完整提示词的通路（AutoDream、Goal 子目标）必须传本常量，否则 MAF 会把通用指令
    /// 叠加到一段从未与它们一起合成的提示词上。
    /// </remarks>
    public const string SuppressFrameworkDefaults = "";

    /// <summary>
    /// 把 OneCode 的产品侧关闭项应用到一个已构造的 options 实例上。
    /// 不触碰通路相关的字段，例如 <see cref="HarnessAgentOptions.DisableToolAutoApproval"/>
    /// 与 <see cref="HarnessAgentOptions.MaximumIterationsPerRequest"/>。
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
