using OneCode.App.Services.Compact;
using OneCode.Core.Domain;
using OneCode.Tests.TestSupport;
namespace OneCode.Tests;

/// <summary>
/// Tests for TokenBudget — estimation logic.
/// </summary>
public sealed class TokenBudgetTests
{
    [Fact]
    public void Estimate_EmptySession_ReturnsBaselineTokens()
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Model = "claude-sonnet-4-20250514"
        };

        var result = TokenBudget.Estimate(session, TestTokenEstimators.Default);

        result.EstimatedInputTokens.Should().Be(0);
        result.MaxInputTokens.Should().BeGreaterThan(0);
        result.RemainingTokens.Should().Be(result.MaxInputTokens,
            "empty session has no estimated tokens, so remaining equals max");
    }

    [Fact]
    public void Estimate_WithMessages_IncreasesTokenCount()
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Model = "claude-sonnet-4-20250514"
        };
        session.Messages.Add(new UserMessage("1", "Hello world", DateTimeOffset.Now));

        var result = TokenBudget.Estimate(session, TestTokenEstimators.Default);

        result.EstimatedInputTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Estimate_WithSystemPrompt_IncludesSystemTokens()
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Model = "claude-sonnet-4-20250514"
        };

        var withoutPrompt = TokenBudget.Estimate(session, TestTokenEstimators.Default);
        var withPrompt = TokenBudget.Estimate(session, TestTokenEstimators.Default, "You are a helpful assistant.");

        withPrompt.EstimatedInputTokens.Should().BeGreaterThan(withoutPrompt.EstimatedInputTokens);
    }
}
