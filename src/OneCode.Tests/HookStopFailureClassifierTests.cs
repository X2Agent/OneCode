using System.Net;
using OneCode.Core.Hooks;
using OneCode.App.Services.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookStopFailureClassifier 单测——覆盖：
///   消息级信号（rate limit / 认证 / 计费 / 无效请求 / 服务端 / 输出 token 上限）
///   → HttpRequestException.StatusCode 判定
///   → 异常链展开（InnerException / AggregateException）
///   → 兜底 unknown
/// </summary>
public sealed class HookStopFailureClassifierTests
{
    [Theory]
    [InlineData("Rate limit exceeded. Please retry after 20s", HookStopFailureClassifier.RateLimit)]
    [InlineData("429 Too Many Requests", HookStopFailureClassifier.RateLimit)]
    [InlineData("Invalid API key provided", HookStopFailureClassifier.AuthFailed)]
    [InlineData("Your credit balance is too low to run this model", HookStopFailureClassifier.Billing)]
    [InlineData("Invalid request: model does not exist", HookStopFailureClassifier.InvalidRequest)]
    [InlineData("The server had an error processing your request", HookStopFailureClassifier.ServerError)]
    [InlineData("This model's maximum output tokens limit was reached", HookStopFailureClassifier.MaxOutputTokens)]
    [InlineData("Something entirely unexpected happened", HookStopFailureClassifier.Unknown)]
    public void Classify_MessageSignals_MapsExpectedCategory(string message, string expected)
    {
        var category = HookStopFailureClassifier.Classify(new InvalidOperationException(message));
        category.Should().Be(expected);
    }

    [Fact]
    public void Classify_HttpRequestExceptionStatus_MapsByStatusCode()
    {
        var rateLimited = new HttpRequestException("Too many requests", inner: null, HttpStatusCode.TooManyRequests);
        HookStopFailureClassifier.Classify(rateLimited).Should().Be(HookStopFailureClassifier.RateLimit);

        var payment = new HttpRequestException("Payment required", inner: null, HttpStatusCode.PaymentRequired);
        HookStopFailureClassifier.Classify(payment).Should().Be(HookStopFailureClassifier.Billing);

        var unauthorized = new HttpRequestException("Unauthorized", inner: null, HttpStatusCode.Unauthorized);
        HookStopFailureClassifier.Classify(unauthorized).Should().Be(HookStopFailureClassifier.AuthFailed);

        var badRequest = new HttpRequestException("Bad request", inner: null, HttpStatusCode.BadRequest);
        HookStopFailureClassifier.Classify(badRequest).Should().Be(HookStopFailureClassifier.InvalidRequest);

        var serverError = new HttpRequestException("Internal error", inner: null, HttpStatusCode.BadGateway);
        HookStopFailureClassifier.Classify(serverError).Should().Be(HookStopFailureClassifier.ServerError);
    }

    [Fact]
    public void Classify_InnerExceptionChain_UsesInnerSignal()
    {
        var wrapped = new InvalidOperationException("Agent run failed",
            new HttpRequestException("Rate limit exceeded", inner: null, HttpStatusCode.TooManyRequests));

        HookStopFailureClassifier.Classify(wrapped).Should().Be(HookStopFailureClassifier.RateLimit);
    }

    [Fact]
    public void Classify_AggregateException_FlattensToInnerSignal()
    {
        var aggregate = new AggregateException(
            new HttpRequestException("Request failed",
                new HttpRequestException("Service unavailable", inner: null, HttpStatusCode.ServiceUnavailable)));

        HookStopFailureClassifier.Classify(aggregate).Should().Be(HookStopFailureClassifier.ServerError);
    }

    [Fact]
    public void Classify_NullException_ReturnsUnknown()
    {
        HookStopFailureClassifier.Classify(null).Should().Be(HookStopFailureClassifier.Unknown);
    }

    [Fact]
    public void Classify_KnownCategories_AlignWithMetadataRegistry()
    {
        var registryCategories = HookEventMetadataRegistry.All
            .Single(m => m.Name == nameof(HookEvent.StopFailure))
            .Matcher!
            .KnownValues;

        var classifierCategories = new[]
        {
            HookStopFailureClassifier.RateLimit,
            HookStopFailureClassifier.AuthFailed,
            HookStopFailureClassifier.Billing,
            HookStopFailureClassifier.InvalidRequest,
            HookStopFailureClassifier.ServerError,
            HookStopFailureClassifier.MaxOutputTokens,
            HookStopFailureClassifier.Unknown,
        };

        classifierCategories.Should().OnlyContain(c => registryCategories.Contains(c));
    }
}
