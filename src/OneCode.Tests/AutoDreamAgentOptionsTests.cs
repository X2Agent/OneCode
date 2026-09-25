using Microsoft.Agents.AI;
using OneCode.App.Services.AutoDream;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// Guards the AutoDream consolidation agent's provider mounts.
/// </summary>
/// <remarks>
/// AutoDream 绕过 <see cref="PipelineProfile"/> 自建 <see cref="HarnessAgentOptions"/>，
/// 按 profile 决定的 Todo/FileMemory 能力决策到不了这条路径——不显式关闭时
/// Harness 对两者默认挂载（审计文档「不无差别开启 AutoDream 等路径」的守卫）。
/// 公共 <see cref="OneCodeHarnessDefaults.ApplyProductOptOuts"/> 按契约必须保持
/// profile 无关，所以本路径的决策只能在这里断言。
/// </remarks>
public sealed class AutoDreamAgentOptionsTests
{
    private static HarnessAgentOptions BuildOptions() => AutoDreamService.BuildConsolidationAgentOptions(
        "fast-model",
        [],
        maxOutputTokens: 4096,
        maxToolCalls: 30);

    [Fact]
    public void BuildConsolidationAgentOptions_DisablesSessionScopedProviders()
    {
        var options = BuildOptions();

        options.DisableTodoProvider.Should().BeTrue(
            "a fixed-prompt single-run consolidation agent has no use for a per-session checklist");
        options.DisableFileMemory.Should().BeTrue(
            "provider-injected tools bypass the ChatOptions allowlist, so the default mount would hand this read-only agent a write surface");
    }

    /// <summary>
    /// 反证：提取出厂方法不得丢掉原有的 harness 默认抑制——空串（非 null）是
    /// 「有意抑制」与「用 MAF 默认文案」的分界，回归成 null 会扩大指令面。
    /// </summary>
    [Fact]
    public void BuildConsolidationAgentOptions_KeepsFrameworkDefaultsSuppressed()
    {
        var options = BuildOptions();

        options.HarnessInstructions.Should().Be(OneCodeHarnessDefaults.SuppressFrameworkDefaults);
        options.HarnessInstructions.Should().BeEmpty();
    }

    /// <summary>
    /// 出厂方法内仍须应用公共 opt-out：AgentMode/Skills/WebSearch 的替代实现
    /// 在产品侧，漏 Apply 会把两套能力同时挂上。
    /// </summary>
    [Fact]
    public void BuildConsolidationAgentOptions_KeepsReplacedHarnessDefaultsDisabled()
    {
        var options = BuildOptions();

        options.DisableAgentModeProvider.Should().BeTrue();
        options.DisableAgentSkillsProvider.Should().BeTrue();
        options.DisableWebSearch.Should().BeTrue();
    }
}
