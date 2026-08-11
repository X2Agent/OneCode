using System.Net;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="PromptTooLongDetector"/>.
/// 验证 Run 级的 prompt-too-long 检测逻辑覆盖 HTTP 413 和关键词检测。
/// </summary>
public sealed class PromptTooLongDetectorTests
{
    // IsPromptTooLong(HttpRequestException)

    [Fact]
    public void IsPromptTooLong_HttpRequestException_413Status_ReturnsTrue()
    {
        var ex = new HttpRequestException("Request entity too large", null, HttpStatusCode.RequestEntityTooLarge);

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_HttpRequestException_MessageContainsPromptIsTooLong_ReturnsTrue()
    {
        var ex = new HttpRequestException("Error: prompt is too long. Please reduce input.", null, HttpStatusCode.BadRequest);

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_HttpRequestException_MessageContainsPromptTooLong_ReturnsTrue()
    {
        var ex = new HttpRequestException("Error: prompt_too_long", null, HttpStatusCode.BadRequest);

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_HttpRequestException_MessageContainsContextLengthExceeded_ReturnsTrue()
    {
        var ex = new HttpRequestException("context_length_exceeded", null, HttpStatusCode.BadRequest);

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_HttpRequestException_UnrelatedError_ReturnsFalse()
    {
        var ex = new HttpRequestException("Internal server error", null, HttpStatusCode.InternalServerError);

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeFalse();
    }

    // IsPromptTooLong(Exception)

    [Fact]
    public void IsPromptTooLong_Exception_MessageContainsKeyword_ReturnsTrue()
    {
        var ex = new InvalidOperationException("prompt is too long for this model");

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_Exception_UnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Some other error");

        PromptTooLongDetector.IsPromptTooLong(ex).Should().BeFalse();
    }

    // ContainsKeyword

    [Theory]
    [InlineData("prompt is too long")]
    [InlineData("Error: prompt is too long for model")]
    [InlineData("PROMPT IS TOO LONG")]
    [InlineData("prompt_too_long")]
    [InlineData("context_length_exceeded")]
    [InlineData("This request failed: Context_Length_Exceeded")]
    public void ContainsKeyword_MatchingText_ReturnsTrue(string text)
    {
        PromptTooLongDetector.ContainsKeyword(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Some unrelated error message")]
    [InlineData("max_output_tokens exceeded")]
    [InlineData("529 overloaded")]
    public void ContainsKeyword_NonMatchingText_ReturnsFalse(string? text)
    {
        PromptTooLongDetector.ContainsKeyword(text).Should().BeFalse();
    }
}
