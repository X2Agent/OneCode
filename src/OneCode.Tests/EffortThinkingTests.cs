using OneCode.Core.Domain;

namespace OneCode.Tests;

public sealed class EffortThinkingTests
{
    [Theory]
    [InlineData("low", EffortLevel.Low)]
    [InlineData("medium", EffortLevel.Medium)]
    [InlineData("high", EffortLevel.High)]
    [InlineData("max", EffortLevel.Max)]
    [InlineData(null, EffortLevel.Medium)]
    [InlineData("invalid", EffortLevel.Medium)]
    [InlineData("HIGH", EffortLevel.High)]
    public void ParseEffort_ReturnsCorrectLevel(string? input, EffortLevel expected)
    {
        EffortThinking.ParseEffort(input).Should().Be(expected);
    }

    [Fact]
    public void GetThinkingBudget_LowEffort_IsSmallerThanBase()
    {
        var low = EffortThinking.GetThinkingBudget(EffortLevel.Low, "claude-sonnet-4-6");
        var medium = EffortThinking.GetThinkingBudget(EffortLevel.Medium, "claude-sonnet-4-6");

        low.Should().BeLessThan(medium);
    }

    [Fact]
    public void GetThinkingBudget_MaxEffort_IsLargerThanBase()
    {
        var max = EffortThinking.GetThinkingBudget(EffortLevel.Max, "claude-sonnet-4-6");
        var high = EffortThinking.GetThinkingBudget(EffortLevel.High, "claude-sonnet-4-6");

        max.Should().BeGreaterThan(high);
    }

    [Fact]
    public void GetThinkingBudget_OpusModel_HasHigherBase()
    {
        var opus = EffortThinking.GetThinkingBudget(EffortLevel.High, "claude-opus-4-6");
        var sonnet = EffortThinking.GetThinkingBudget(EffortLevel.High, "claude-sonnet-4-6");

        opus.Should().BeGreaterThan(sonnet);
    }

    [Theory]
    [InlineData(ThinkingMode.Disabled, EffortLevel.Low, false)]
    [InlineData(ThinkingMode.Disabled, EffortLevel.Max, false)]
    [InlineData(ThinkingMode.Enabled, EffortLevel.Low, true)]
    [InlineData(ThinkingMode.Enabled, EffortLevel.Max, true)]
    [InlineData(ThinkingMode.Adaptive, EffortLevel.Low, false)]
    [InlineData(ThinkingMode.Adaptive, EffortLevel.Medium, true)]
    [InlineData(ThinkingMode.Adaptive, EffortLevel.High, true)]
    [InlineData(ThinkingMode.Adaptive, EffortLevel.Max, true)]
    public void ShouldEnableThinking_ReturnsExpected(ThinkingMode mode, EffortLevel effort, bool expected)
    {
        EffortThinking.ShouldEnableThinking(mode, effort).Should().Be(expected);
    }
}
