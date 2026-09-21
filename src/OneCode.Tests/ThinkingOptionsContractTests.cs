using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// 思考/推理意图的产出侧契约：必须走 MEAI 标准的 <see cref="ChatOptions.Reasoning"/>，
/// 不得写入 provider 私有的 <c>AdditionalProperties</c> key。
/// </summary>
/// <remarks>
/// 背景：<c>BuildChatOptions</c> 曾写 <c>thinking_budget</c> / <c>thinking_effort</c>，
/// 由 <c>ProviderAwareDecorator</c> 转成 <c>thinking</c> / <c>reasoning_effort</c>。这些 key
/// 不是任何 SDK 的契约，Anthropic 适配器只读 <c>ChatOptions.Reasoning</c>，于是扩展思考
/// 从未生效。<see cref="ProviderWireContractTests"/> 从 wire 层守「真的发出去了」，
/// 本文件守「产出侧用的是标准字段」。另守 <c>MaxOutputTokens</c> 与推理预算的解耦：
/// 显式设了 max_tokens 时 Anthropic 会把 budget 压到 <c>max_tokens - 1</c>，输出预算被吃光。
/// </remarks>
public sealed class ThinkingOptionsContractTests
{
    [Theory]
    [InlineData(0, ReasoningEffort.None)]
    [InlineData(-1, ReasoningEffort.None)]
    [InlineData(1, ReasoningEffort.Low)]
    [InlineData(1024, ReasoningEffort.Low)]
    [InlineData(1025, ReasoningEffort.Medium)]
    [InlineData(8192, ReasoningEffort.Medium)]
    [InlineData(8193, ReasoningEffort.High)]
    [InlineData(16384, ReasoningEffort.High)]
    [InlineData(16385, ReasoningEffort.ExtraHigh)]
    [InlineData(32000, ReasoningEffort.ExtraHigh)]
    public void ToReasoningEffort_RoundsUpToTheSmallestSufficientLevel(int budget, ReasoningEffort expected)
        => EffortThinking.ToReasoningEffort(budget).Should().Be(expected,
            "the level must never represent less thinking budget than the user asked for");

    [Fact]
    public void BuildChatOptions_ThinkingEnabled_UsesTypedReasoningAndNoPrivateKeys()
    {
        var options = new MainAgentRunOptions
        {
            SystemPrompt = "body",
            UserPrompt = "hello",
            EnableThinking = true,
            ThinkingBudgetTokens = 16_384,
        };

        var chatOptions = MainAgentRunner.BuildChatOptions(options);

        chatOptions.Reasoning.Should().NotBeNull("MEAI exposes reasoning as a typed option");
        chatOptions.Reasoning!.Effort.Should().Be(ReasoningEffort.High);
        chatOptions.AdditionalProperties.Should().BeNull(
            "provider wire keys are not a contract any SDK reads");
    }

    [Fact]
    public void BuildChatOptions_ThinkingEnabled_DoesNotForceTheOutputCapThatWouldStarveThinking()
    {
        var options = new MainAgentRunOptions
        {
            UserPrompt = "hello",
            EnableThinking = true,
            ThinkingBudgetTokens = 16_384,
        };

        MainAgentRunner.BuildChatOptions(options).MaxOutputTokens.Should().BeNull(
            "an explicit max_tokens makes Anthropic clamp the thinking budget to max_tokens - 1");
    }

    [Fact]
    public void BuildChatOptions_ThinkingEnabled_KeepsACallerSuppliedOutputCap()
    {
        var options = new MainAgentRunOptions
        {
            UserPrompt = "hello",
            EnableThinking = true,
            ThinkingBudgetTokens = 1_024,
            MaxOutputTokens = 64_000,
        };

        var chatOptions = MainAgentRunner.BuildChatOptions(options);

        chatOptions.MaxOutputTokens.Should().Be(64_000, "the caller's explicit cap is authoritative");
        chatOptions.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
    }

    [Fact]
    public void BuildChatOptions_ThinkingDisabled_KeepsTheDefaultOutputCapAndNoReasoning()
    {
        var chatOptions = MainAgentRunner.BuildChatOptions(new MainAgentRunOptions { UserPrompt = "hello" });

        chatOptions.Reasoning.Should().BeNull("an unset effort must let the provider use its own default");
        chatOptions.MaxOutputTokens.Should().Be(4096);
    }

    [Fact]
    public void BuildChatOptions_ThinkingFlagWithoutAnyBudget_DoesNotInventAReasoningLevel()
    {
        var options = new MainAgentRunOptions
        {
            UserPrompt = "hello",
            EnableThinking = true,
            ThinkingBudgetTokens = 0,
        };

        MainAgentRunner.BuildChatOptions(options).Reasoning.Should().BeNull();
    }

    [Fact]
    public void BuildChatOptions_ExplicitEffortString_WinsOverTheDerivedBudget()
    {
        var options = new MainAgentRunOptions
        {
            UserPrompt = "hello",
            EnableThinking = true,
            ThinkingBudgetTokens = 16_384,
            ThinkingEffort = "low",
        };

        MainAgentRunner.BuildChatOptions(options).Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
    }
}
