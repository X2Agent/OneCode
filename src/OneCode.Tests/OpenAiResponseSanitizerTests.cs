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

    // OpenAI 兼容端点在流式 delta 的 tool_calls 里发送空 type 时，
    // 官方 SDK 的 ToChatToolCallKind 抛 ArgumentOutOfRangeException 中断整条流——
    // 消毒层必须把空/缺失 type 归一为合法值（真实故障形态，见用户上报堆栈）。
    [Fact]
    public void SanitizePayload_StreamDeltaToolCallEmptyType_RewritesToFunction()
    {
        const string payload =
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"","id":"call_1","function":{"name":"Read","arguments":"{}"}}]}}]}""";

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(payload);

        sanitized.Should().Contain(@"""type"":""function""");
        sanitized.Should().Contain(@"""id"":""call_1""");
        sanitized.Should().Contain(@"""name"":""Read""");
        sanitized.Should().NotContain(@"""type"":""""");
    }

    // SDK 对缺失 type 的 tool_call 是安全跳过的（默认 Function），消毒层不得越权补写。
    [Fact]
    public void SanitizePayload_ToolCallMissingType_IsLeftUnchanged()
    {
        const string payload =
            """{"delta":{"tool_calls":[{"index":0,"id":"call_2","function":{"name":"Edit","arguments":"{}"}}]}}""";

        OpenAiResponseSanitizer.SanitizePayload(payload).Should().Be(payload);
    }

    // SDK 不支持 custom 工具调用（ChatToolCallKind 仅有 Function）——空 type 一律归一为
    // "function" 保持流存活，而不是映射成 SDK 同样拒绝的 "custom"。
    [Fact]
    public void SanitizePayload_EmptyTypeWithCustomField_StillRewritesToFunction()
    {
        const string payload =
            """{"delta":{"tool_calls":[{"index":0,"type":"","custom":{"input":"ls -la"}}]}}""";

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(payload);

        sanitized.Should().Contain(@"""type"":""function""");
        sanitized.Should().Contain(@"""input"":""ls -la""");
    }

    [Fact]
    public void SanitizePayload_NonStreamingMessageToolCallEmptyType_RewritesToFunction()
    {
        const string payload =
            """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c","type":"","function":{"name":"Grep","arguments":"{}"}}]}}]}""";

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(payload);

        sanitized.Should().Contain(@"""type"":""function""");
    }

    [Fact]
    public void SanitizePayload_ValidToolCallType_IsLeftUnchanged()
    {
        const string payload =
            """{"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"Read","arguments":"{}"}}]}}""";

        OpenAiResponseSanitizer.SanitizePayload(payload).Should().Be(payload);
    }

    [Fact]
    public void SanitizePayload_NonToolCallTypeFields_AreUntouched()
    {
        const string payload =
            """{"response_format":{"type":"json_object"},"choices":[{"delta":{"content":[{"type":"text","text":"hi"}]}}]}""";

        // 无 tool_calls 字段：不得触碰其他上下文的 type（content part、response_format）。
        OpenAiResponseSanitizer.SanitizePayload(payload).Should().Be(payload);
    }

    [Theory]
    [InlineData("""{"tool_calls": broken""")]
    public void SanitizePayload_UnnormalizableToolCallPayload_ReturnsUnchanged(string payload)
    {
        OpenAiResponseSanitizer.SanitizePayload(payload).Should().Be(payload);
    }

    [Fact]
    public void SanitizePayload_BareEmptyTypeWithoutKindFields_DefaultsToFunction()
    {
        const string payload = """{"delta":{"tool_calls":[{"index":0,"type":""}]}}""";

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(payload);

        sanitized.Should().Contain(@"""type"":""function""");
    }

    [Fact]
    public void SanitizeSseLine_ToolCallEmptyType_RewritesAndKeepsPrefix()
    {
        const string line =
            """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"","id":"c","function":{"name":"Read","arguments":""}}]}}]}""";

        var sanitized = OpenAiResponseSanitizer.SanitizeSseLine(line);

        sanitized.Should().StartWith("data: ");
        sanitized.Should().Contain(@"""type"":""function""");
        sanitized.Should().NotContain(@"""type"":""""");
    }

    [Fact]
    public void SanitizeSseLine_ReasoningDeltaWithEmptyContent_NormalizesContentToNull()
    {
        const string line =
            """data: {"choices":[{"index":0,"delta":{"content":"","reasoning_content":"step 1"}}]}""";

        var sanitized = OpenAiResponseSanitizer.SanitizeSseLine(line);

        sanitized.Should().Contain(@"""content"":null");
        sanitized.Should().Contain(@"""reasoning_content"":""step 1""");
        sanitized.Should().NotContain(@"""content"":""""");
    }
}
