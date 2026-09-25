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
        // No catalog/registry supplied → 128_000 default context window minus the
        // 8_192 tokens reserved for output.
        result.MaxInputTokens.Should().Be(128_000 - 8_192);
        result.RemainingTokens.Should().Be(result.MaxInputTokens,
            "empty session has no estimated tokens, so remaining equals max");
    }

    [Fact]
    public void Estimate_WithMessages_AddsTextTokensAndMessageOverhead()
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Model = "claude-sonnet-4-20250514"
        };
        session.Messages.Add(new UserMessage("1", "Hello world", DateTimeOffset.Now));

        var result = TokenBudget.Estimate(session, TestTokenEstimators.Default);

        // "Hello world" costs ceil(11 / 4) = 3 tokens plus the 12-token user-message overhead.
        result.EstimatedInputTokens.Should().Be(15);
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

        // "You are a helpful assistant." costs ceil(28 / 4) = 7 tokens, which are added
        // on top of the message tokens.
        withPrompt.EstimatedInputTokens.Should().Be(withoutPrompt.EstimatedInputTokens + 7);
    }
}
