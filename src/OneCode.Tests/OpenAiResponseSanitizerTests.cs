using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>OpenAiResponseSanitizer 的契约：空 choices 响应检测与上游错误体提取。</summary>
public sealed class OpenAiResponseSanitizerTests
{
    [Fact]
    public void HasEmptyChoices_EmptyChoicesArray_ReturnsTrue()
    {
        const string body = """{"id":"x","object":"chat.completion","choices":[],"usage":{"total_tokens":0}}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Fact]
    public void HasEmptyChoices_MissingChoicesOnChatCompletion_ReturnsTrue()
    {
        const string body = """{"id":"x","object":"chat.completion","created":1}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Fact]
    public void HasEmptyChoices_NormalCompletion_ReturnsFalse()
    {
        const string body = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeFalse();
    }

    [Fact]
    public void HasEmptyChoices_ErrorBodyWithoutChoices_ReturnsTrue()
    {
        const string body = """{"error":{"message":"Upstream overloaded","code":503}}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void HasEmptyChoices_NonJsonBody_ReturnsFalse(string body)
    {
        // 非 JSON 体不是补全响应，不得误判触发重试。
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeFalse();
    }

    [Fact]
    public void HasEmptyChoices_JsonArrayOrScalarRoot_ReturnsFalse()
    {
        OpenAiResponseSanitizer.HasEmptyChoices("[1,2,3]").Should().BeFalse();
        OpenAiResponseSanitizer.HasEmptyChoices("\"plain string\"").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"error":{"message":"Upstream error from Nvidia: Service temporarily overloaded","code":502}}""",
        "Upstream error from Nvidia: Service temporarily overloaded", "502")]
    [InlineData("""{"error":"simple string error"}""", "simple string error", null)]
    [InlineData("""{"error":{"metadata":{"raw":"raw upstream failure"},"code":"overloaded"}}""",
        "raw upstream failure", "overloaded")]
    public void TryExtractUpstreamError_ErrorBody_ParsesMessageAndCode(
        string body, string expectedMessage, string? expectedCode)
    {
        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out var code, out _)
            .Should().BeTrue();
        message.Should().Be(expectedMessage);
        code.Should().Be(expectedCode);
    }

    [Fact]
    public void TryExtractUpstreamError_OpenRouterMetadataFields_AreFoldedIntoMessage()
    {
        const string body = """{"error":{"message":"openai_error","code":"bad_response_status_code","metadata":{"error_type":"provider_overloaded","provider_code":502}}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out var code, out _)
            .Should().BeTrue();
        code.Should().Be("bad_response_status_code");
        message.Should().Contain("openai_error");
        message.Should().Contain("[error_type=provider_overloaded]");
        message.Should().Contain("[provider_code=502]");
    }

    [Fact]
    public void TryExtractUpstreamError_OpenAiTypeField_IsFoldedIntoMessage()
    {
        const string body = """{"error":{"message":"The server had an error","type":"server_error","code":500}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out _, out _)
            .Should().BeTrue();
        message.Should().Contain("The server had an error");
        message.Should().Contain("[type=server_error]");
    }

    [Theory]
    [InlineData("""{"error":{"code":429,"message":"Provider returned error","metadata":{"error_type":"rate_limit_exceeded","provider_name":"DeepSeek","raw":"Rate limit exceeded: 3 requests per minute"}}}""")]
    public void TryExtractUpstreamError_MaskedOpenRouterMessage_PrefersRawAsMessage(string body)
    {
        // 用户上报的故障形态：OpenRouter 把上游错误掩码成通用串，真实原因只在 metadata.raw。
        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out var code, out var errorType)
            .Should().BeTrue();

        message.Should().StartWith("Rate limit exceeded");
        message.Should().NotContain("Provider returned error");
        message.Should().Contain("[error_type=rate_limit_exceeded]");
        message.Should().Contain("[provider_name=DeepSeek]");
        message.Should().NotContain("[raw=");
        code.Should().Be("429");
        errorType.Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public void TryExtractUpstreamError_NonMaskedMessage_AppendsProviderNameAndRaw()
    {
        const string body =
            """{"error":{"code":429,"message":"Rate limit exceeded","metadata":{"error_type":"rate_limit_exceeded","provider_name":"DeepSeek","raw":"upstream said slow down","provider_code":"rate_limited"}}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out _, out _)
            .Should().BeTrue();

        message.Should().StartWith("Rate limit exceeded");
        message.Should().Contain("[provider_name=DeepSeek]");
        message.Should().Contain("[provider_code=rate_limited]");
        message.Should().Contain("[raw=upstream said slow down]");
    }

    [Fact]
    public void TryExtractUpstreamError_ErrorTypePositionFollowsOpenRouterSkin()
    {
        // Chat Completions skin：error.metadata.error_type；Anthropic skin：error.error_type。
        OpenAiResponseSanitizer.TryExtractUpstreamError(
            """{"error":{"code":402,"message":"no credits","metadata":{"error_type":"payment_required"}}}""",
            out _, out _, out var chatType).Should().BeTrue();
        chatType.Should().Be("payment_required");

        OpenAiResponseSanitizer.TryExtractUpstreamError(
            """{"error":{"type":"error","error_type":"rate_limit_exceeded","message":"slow down"}}""",
            out _, out _, out var anthropicType).Should().BeTrue();
        anthropicType.Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public void TryExtractUpstreamError_ModerationMetadata_IsFoldedIntoMessage()
    {
        const string body =
            """{"error":{"code":403,"message":"Request blocked","metadata":{"reasons":["hate","violence"],"flagged_input":"some text","error_type":"content_policy_violation"}}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out _, out _)
            .Should().BeTrue();

        message.Should().Contain("[reasons=hate, violence]");
        message.Should().Contain("[flagged_input=some text]");
    }

    [Theory]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}]}""")]
    [InlineData("not json at all")]
    [InlineData("""{"other":"field"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}],"error":null}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"there was an error earlier"}}]}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"}}],"error":{"message":"ignored","code":500}}""")]
    public void TryExtractUpstreamError_NonErrorBody_ReturnsFalse(string body)
    {
        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out _, out _, out _)
            .Should().BeFalse();
    }
}
