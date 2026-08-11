using Microsoft.Extensions.AI;
using OneCode.App.Services.Observability;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Api;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class TokenBreakdownEstimatorCalibrationTests
{
    [Fact]
    public void Estimate_WithCalibratedTracker_AppliesCalibrationFactor()
    {
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(50, 30, 20, 0, 100);
        var breakdown2 = new TokenBreakdown(50, 30, 20, 0, 100);
        tracker.Record(new TokenUsage(200, 50), breakdown1);
        tracker.Record(new TokenUsage(200, 50), breakdown2);
        tracker.CalibrationFactor.Should().BeApproximately(2.0, 0.001);

        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);
        var uncalibrated = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: null);

        var calibratedResult = sut.Estimate("short prompt", null, null);
        var rawResult = uncalibrated.Estimate("short prompt", null, null);

        // 校准系数 2.0 应使 SystemPrompt 估算值翻倍（允许 ±2 误差取整）
        calibratedResult.SystemPrompt.Should().BeInRange(
            rawResult.SystemPrompt * 2 - 2,
            rawResult.SystemPrompt * 2 + 2);
    }

    [Fact]
    public void Estimate_WithActualInputTokens_CalculatesOtherWithCalibration()
    {
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);

        var result = sut.Estimate("prompt", null, null, actualInputTokens: 1000);

        result.Other.Should().Be(1000 - result.SystemPrompt - result.Messages - result.ToolsAndSkills,
            "Other should be the residual when actualInputTokens is provided");
        result.TotalEstimated.Should().Be(1000);
    }

    [Fact]
    public void Estimate_CalibrationFactorPropagatesToAllScenarios()
    {
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var b1 = new TokenBreakdown(100, 60, 40, 0, 200);
        var b2 = new TokenBreakdown(100, 60, 40, 0, 200);
        tracker.Record(new TokenUsage(400, 50), b1);
        tracker.Record(new TokenUsage(400, 50), b2);

        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);
        var raw = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: null);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
        };

        var calibrated = sut.Estimate("system prompt", null, messages);
        var rawResult = raw.Estimate("system prompt", null, messages);

        calibrated.SystemPrompt.Should().BeInRange(
            rawResult.SystemPrompt * 2 - 2,
            rawResult.SystemPrompt * 2 + 2);
        calibrated.Messages.Should().BeInRange(
            rawResult.Messages * 2 - 2,
            rawResult.Messages * 2 + 2);
    }
}
