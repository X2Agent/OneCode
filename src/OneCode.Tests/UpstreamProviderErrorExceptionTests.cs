using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>UpstreamProviderErrorException 的契约：HTTP 状态码/类型码的瞬时性分类与提示推导。</summary>
public sealed class UpstreamProviderErrorExceptionTests
{
    [Theory]
    [InlineData("502", true)]
    [InlineData("429", true)]
    [InlineData("408", true)]
    [InlineData("401", false)]
    [InlineData("400", false)]
    public void ClassifyTransient_NumericCode_FollowsHttpStatusSemantics(string code, bool expectedTransient)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "any message").Should().Be(expectedTransient);
    }

    [Fact]
    public void ClassifyTransient_NonNumericCode_UsesMessageKeywords()
    {
        UpstreamProviderErrorException.ClassifyTransient("overloaded", "upstream busy")
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("insufficient_quota", "quota exceeded")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_GatewayRelayError_IsTransientByDefault()
    {
        // 用户实际遇到的故障：网关掩码错误体（HTTP 400 外壳）必须默认瞬时可重试。
        UpstreamProviderErrorException.ClassifyTransient("bad_response_status_code", "openai_error", 400)
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("bad_response_status_code", "openai_error")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("bad_response_status_code")]
    [InlineData("bad_response")]
    [InlineData("upstream_error")]
    [InlineData("do_request_failed")]
    [InlineData("read_response_body_failed")]
    [InlineData("new_api_error")]
    [InlineData("openai_error")]
    [InlineData("api_error")]
    [InlineData("server_error")]
    [InlineData("internal_error")]
    [InlineData("unmapped")]
    [InlineData("provider_error")]
    [InlineData("provider_overloaded")]
    [InlineData("provider_unavailable")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("timeout")]
    public void ClassifyTransient_GatewayRelayCodes_DefaultToTransient(string code)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "opaque gateway failure")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("context_length_exceeded")]
    [InlineData("authentication")]
    [InlineData("payment_required")]
    [InlineData("content_policy_violation")]
    [InlineData("refusal")]
    [InlineData("invalid_request_error")]
    [InlineData("model_not_found")]
    [InlineData("permission_error")]
    [InlineData("not_found_error")]
    [InlineData("insufficient_quota")]
    [InlineData("invalid_api_key")]
    [InlineData("billing_hard_limit_reached")]
    [InlineData("model_not_exists")]
    public void ClassifyTransient_KnownPermanentCodes_FailFast(string code)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "opaque gateway failure")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_PermanentSignatureBeatsGatewayRelayCode()
    {
        UpstreamProviderErrorException.ClassifyTransient(
                "bad_response_status_code", "Invalid API key provided", 400)
            .Should().BeFalse();
        UpstreamProviderErrorException.ClassifyTransient(
                "openai_error", "This model's maximum context length is 8192 tokens", 400)
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_PermanentSignatureMatchesUnderscoredCode()
    {
        UpstreamProviderErrorException.ClassifyTransient("invalid_api_key", "request rejected")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_OpaqueCodeWithTransientHttpStatus_IsTransient()
    {
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 502)
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 504)
            .Should().BeTrue();
    }

    [Fact]
    public void ClassifyTransient_UnknownCodeWithPermanentHttpStatus_StaysPermanent()
    {
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 400)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("rate_limit_exceeded")]
    [InlineData("provider_overloaded")]
    [InlineData("provider_unavailable")]
    [InlineData("timeout")]
    [InlineData("server")]
    [InlineData("unmapped")]
    public void ClassifyTransient_OpenRouterTypedCodes_TransientSet(string errorType)
    {
        UpstreamProviderErrorException.ClassifyTransient("429", "opaque", 429, errorType)
            .Should().BeTrue($"error_type={errorType} must stay retryable");
    }

    [Theory]
    [InlineData("payment_required")]
    [InlineData("authentication")]
    [InlineData("permission_denied")]
    [InlineData("context_length_exceeded")]
    [InlineData("max_tokens_exceeded")]
    [InlineData("token_limit_exceeded")]
    [InlineData("string_too_long")]
    [InlineData("invalid_request")]
    [InlineData("invalid_prompt")]
    [InlineData("not_found")]
    [InlineData("precondition_failed")]
    [InlineData("payload_too_large")]
    [InlineData("unprocessable")]
    [InlineData("content_policy_violation")]
    [InlineData("refusal")]
    [InlineData("invalid_image")]
    [InlineData("image_too_large")]
    [InlineData("image_too_small")]
    [InlineData("unsupported_image_format")]
    [InlineData("image_not_found")]
    [InlineData("image_download_failed")]
    public void ClassifyTransient_OpenRouterTypedCodes_PermanentSet(string errorType)
    {
        UpstreamProviderErrorException.ClassifyTransient("429", "opaque", 429, errorType)
            .Should().BeFalse($"error_type={errorType} must fail fast");
    }

    [Fact]
    public void ClassifyTransient_TypedCodeBeatsHttpShellStatus()
    {
        // 用户上报的故障：OpenRouter 把余额不足包成 429 外壳，类型码必须一票否决。
        UpstreamProviderErrorException.ClassifyTransient("429", "Provider returned error", 429, "payment_required")
            .Should().BeFalse();
        UpstreamProviderErrorException.ClassifyTransient("400", "Provider returned error", 400, "context_length_exceeded")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_UnknownTypedCode_FallsBackToStatusSemantics()
    {
        UpstreamProviderErrorException.ClassifyTransient("429", "some failure", 429, "brand_new_type")
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("401", "some failure", 401, "brand_new_type")
            .Should().BeFalse();
    }

    [Fact]
    public void Exception_CarriesHttpStatusCode()
    {
        var ex = new UpstreamProviderErrorException(
            "Provider returned HTTP 400 with an upstream error body", "bad_response_status_code", 400);
        ex.HttpStatusCode.Should().Be(400);
        ex.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Exception_CarriesUpstreamDetails_AndTransientFlag()
    {
        var ex = new UpstreamProviderErrorException(
            "Upstream error from Nvidia: Service temporarily overloaded", "502");
        ex.UpstreamErrorCode.Should().Be("502");
        ex.IsTransient.Should().BeTrue();
        ex.Message.Should().Contain("Nvidia");
    }

    [Fact]
    public void Exception_CarriesErrorTypeAndRetryAfter()
    {
        var ex = new UpstreamProviderErrorException(
            "rate limited", "429", 429, "rate_limit_exceeded", TimeSpan.FromSeconds(30));

        ex.ErrorType.Should().Be("rate_limit_exceeded");
        ex.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        ex.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Exception_PaymentRequiredWith429Shell_FailsFast()
    {
        var ex = new UpstreamProviderErrorException(
            "Provider returned HTTP 429 with an upstream error body", "429", 429, "payment_required");

        ex.IsTransient.Should().BeFalse();
    }

    [Theory]
    [InlineData("payment_required", "credits")]
    [InlineData("authentication", "key")]
    [InlineData("rate_limit_exceeded", "Rate limited")]
    [InlineData("context_length_exceeded", "compact")]
    [InlineData("max_tokens_exceeded", "max output tokens")]
    [InlineData("provider_overloaded", "retry")]
    [InlineData("content_policy_violation", "rephrase")]
    [InlineData("not_found", "model ID")]
    public void BuildHint_KnownErrorType_ReturnsActionableGuidance(string errorType, string expectedFragment)
    {
        UpstreamProviderErrorException.BuildHint(errorType).Should().Contain(expectedFragment);
    }

    [Fact]
    public void BuildHint_UnknownErrorType_ReturnsNull()
    {
        UpstreamProviderErrorException.BuildHint("brand_new_type").Should().BeNull();
        UpstreamProviderErrorException.BuildHint(null).Should().BeNull();
    }

    [Theory]
    [InlineData("qwen/qwen3.8-27b:free is temporarily rate-limited upstream. Please retry shortly, or add your own key to accumulate your rate limits")]
    [InlineData("upstream shared pool exhausted, retry later")]
    public void BuildHint_SharedPoolRateLimit_SuggestsModelSwitchOrByok(string upstreamMessage)
    {
        // 用户实际遇到的免费模型 429：无 error_type 的旧式掩码体，按原文签名兜底。
        UpstreamProviderErrorException.BuildHint(null, upstreamMessage)
            .Should().Contain("/config set global model");
    }

    [Fact]
    public void BuildHint_SharedPoolSignature_IgnoredWhenTypedCodePresent()
    {
        // 有类型码时走类型码提示，不叠加原文兜底。
        UpstreamProviderErrorException.BuildHint("rate_limit_exceeded", "temporarily rate-limited upstream")
            .Should().NotContain("/config set global model");
    }
}
