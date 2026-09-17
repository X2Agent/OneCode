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

        options.DisableTodoProvider.Should().BeTrue();
        options.DisableAgentModeProvider.Should().BeTrue();
        options.DisableFileMemory.Should().BeTrue();
        options.DisableAgentSkillsProvider.Should().BeTrue();
        options.DisableWebSearch.Should().BeTrue();
        options.DisableCompaction.Should().BeTrue();
        options.HarnessInstructions.Should().BeEmpty();
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
