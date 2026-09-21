using Microsoft.Agents.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

public sealed class OneCodeHarnessDefaultsTests
{
    [Fact]
    public void ApplyProductOptOuts_DisablesReplacedHarnessDefaults()
    {
        var options = new HarnessAgentOptions();

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.DisableAgentModeProvider.Should().BeTrue();
        options.DisableAgentSkillsProvider.Should().BeTrue();
        options.DisableWebSearch.Should().BeTrue();
    }

    /// <summary>
    /// Todo 与 FileMemory 不再由公共 opt-out 强制关闭：两者都是按 profile 决定的能力，
    /// 全局关闭会让该决定无法生效，全局开启则会把写工具交给只读 Agent。
    /// </summary>
    [Fact]
    public void ApplyProductOptOuts_LeavesProfileScopedCapabilitiesOpen()
    {
        var options = new HarnessAgentOptions();

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.DisableTodoProvider.Should().BeFalse();
        options.DisableFileMemory.Should().BeFalse();
    }

    /// <summary>
    /// Harness 现在是压缩的唯一所有者：产品通过 <see cref="HarnessAgentOptions.CompactionStrategy"/>
    /// 提供策略，opt-out 不得把压缩关掉（关掉等于静默丢弃调用方提供的策略）。
    /// </summary>
    [Fact]
    public void ApplyProductOptOuts_LeavesCompactionToHarness()
    {
        var options = new HarnessAgentOptions();

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.DisableCompaction.Should().BeFalse();
    }

    /// <summary>
    /// opt-out 不得覆盖调用方的指令配置：传 null 表示要 MAF 默认文案，
    /// 传空串表示有意抑制，两者都是调用方的决定。
    /// </summary>
    [Fact]
    public void ApplyProductOptOuts_DoesNotOverrideCallerInstructions()
    {
        var withDefault = new HarnessAgentOptions();
        OneCodeHarnessDefaults.ApplyProductOptOuts(withDefault);
        withDefault.HarnessInstructions.Should().BeNull("null means 'use MAF defaults', not 'suppress'");

        var withProductText = new HarnessAgentOptions { HarnessInstructions = "product text" };
        OneCodeHarnessDefaults.ApplyProductOptOuts(withProductText);
        withProductText.HarnessInstructions.Should().Be("product text");

        var withSuppression = new HarnessAgentOptions { HarnessInstructions = "" };
        OneCodeHarnessDefaults.ApplyProductOptOuts(withSuppression);
        withSuppression.HarnessInstructions.Should().BeEmpty("an explicit suppression must be preserved");
    }

    [Fact]
    public void ApplyProductOptOuts_DoesNotOverridePathSpecificFields()
    {
        var options = new HarnessAgentOptions
        {
            DisableToolAutoApproval = true,
            MaximumIterationsPerRequest = 42,
            Name = "keep-me",
        };

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.DisableToolAutoApproval.Should().BeTrue();
        options.MaximumIterationsPerRequest.Should().Be(42);
        options.Name.Should().Be("keep-me");
    }

    /// <summary>
    /// Guard against re-introducing dual-mounted Harness capabilities.
    ///
    /// <para>
    /// These options are all opt-in (MAF leaves them null/empty), so today there is nothing to
    /// disable. They are asserted here so that a future change which turns one of them on — thereby
    /// mounting a second file-access / background-agent / loop-evaluator capability alongside the
    /// product implementation — fails this test instead of silently starting a dual mount.
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyProductOptOuts_LeavesOptInHarnessFeaturesOff()
    {
        var options = new HarnessAgentOptions();

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.FileAccessStore.Should().BeNull();
        options.FileAccessProviderOptions.Should().BeNull();
        options.BackgroundAgents.Should().BeNull();
        options.BackgroundAgentsProviderOptions.Should().BeNull();
        options.LoopEvaluators.Should().BeNull();
        options.LoopAgentOptions.Should().BeNull();
        options.CompactionStrategy.Should().BeNull();
        options.AgentSkillsSource.Should().BeNull();
    }

    /// <summary>
    /// OpenTelemetry must stay enabled: it is the sole tool-call observability source now that the
    /// product state machine no longer keeps its own recent-tool-call buffer.
    /// </summary>
    [Fact]
    public void ApplyProductOptOuts_KeepsOpenTelemetryEnabled()
    {
        var options = new HarnessAgentOptions();

        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        options.DisableOpenTelemetry.Should().BeFalse();
    }
}
