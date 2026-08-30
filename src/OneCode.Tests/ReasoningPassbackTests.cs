using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Ai;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// DeepSeek thinking 模式 reasoning_content 回传测试：
/// thinking + 工具调用场景下，DeepSeek 要求历史 assistant 消息携带
/// reasoning_content 原样回传，缺失时报 HTTP 400 invalid_request_error。
/// </summary>
public sealed class ReasoningPassbackTests
{
    // —— ReasoningPassbackPolicy ——

    [Theory]
    [InlineData("openai", "https://api.deepseek.com/v1", "deepseek-chat", true)]
    [InlineData("deepseek", "https://api.example.com/v1", "gpt-4o", true)]
    [InlineData("openai", "https://openrouter.ai/api/v1", "deepseek/deepseek-v4-flash", true)]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4o", false)]
    [InlineData("openai", null, null, false)]
    public void ShouldEnable_MatchesDeepSeekOnly(string? providerId, string? baseUrl, string? model, bool expected)
    {
        ReasoningPassbackPolicy.ShouldEnable(providerId, baseUrl, model).Should().Be(expected);
    }

    // —— ReasoningPassbackContext.Capture ——

    [Fact]
    public void Capture_AssistantReasoningTexts_CollectedInOrder()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "查一下天气"),
            new(ChatRole.Assistant, [
                new TextReasoningContent("先查日期，再调工具。"),
                new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>()),
            ]),
            new(ChatRole.User, [new FunctionResultContent("call-1", "Cloudy")]),
            new(ChatRole.Assistant, "今天多云。"),
        };

        var scope = ReasoningPassbackContext.Capture(messages);

        scope.AssistantReasonings.Should().HaveCount(2);
        scope.AssistantReasonings[0].Should().Be("先查日期，再调工具。");
        scope.AssistantReasonings[1].Should().BeEmpty();
        scope.HasReasoning.Should().BeTrue();
    }

    [Fact]
    public void Capture_NoAssistantReasoning_HasReasoningFalse()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, "hello"),
        };

        var scope = ReasoningPassbackContext.Capture(messages);

        scope.AssistantReasonings.Should().ContainSingle().Which.Should().BeEmpty();
        scope.HasReasoning.Should().BeFalse();
    }

    // —— OpenAiReasoningPassbackHandler.RewriteRequestBody ——

    [Fact]
    public void RewriteRequestBody_MissingReasoningContent_InjectedAfterRole()
    {
        const string body = """
            {"model":"deepseek-v4-flash","messages":[
                {"role":"user","content":"查天气"},
                {"role":"assistant","content":"","tool_calls":[{"id":"call-1","type":"function","function":{"name":"get_weather","arguments":"{}"}}]},
                {"role":"tool","tool_call_id":"call-1","content":"Cloudy"}
            ]}
            """;

        var patched = OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["先查日期。"]);

        patched.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patched!);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);
        messages[1].TryGetProperty("reasoning_content", out var reasoning).Should().BeTrue();
        reasoning.GetString().Should().Be("先查日期。");
        // tool 消息不受影响
        messages[2].TryGetProperty("reasoning_content", out _).Should().BeFalse();
    }

    [Fact]
    public void RewriteRequestBody_ExistingReasoningContent_LeftUntouched()
    {
        // 第一条 assistant 已带 reasoning_content（保持原样），第二条缺失（注入新值）。
        const string body = """
            {"model":"m","messages":[
                {"role":"assistant","content":"ok","reasoning_content":"原样保留"},
                {"role":"user","content":"next"},
                {"role":"assistant","content":"b"}
            ]}
            """;

        var patched = OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["新值", "注入值"]);

        patched.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patched!);
        var messages = doc.RootElement.GetProperty("messages");
        messages[0].GetProperty("reasoning_content").GetString().Should().Be("原样保留");
        messages[2].GetProperty("reasoning_content").GetString().Should().Be("注入值");
    }

    [Fact]
    public void RewriteRequestBody_AllAssistantsAlreadyCarryReasoning_ReturnsNull()
    {
        // 全部 assistant 已有 reasoning_content → 无需改动，返回 null 不重建请求体。
        const string body = """
            {"model":"m","messages":[
                {"role":"assistant","content":"ok","reasoning_content":"原样保留"}
            ]}
            """;

        OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["新值"])
            .Should().BeNull();
    }

    [Fact]
    public void RewriteRequestBody_EmptySlot_PreservedAndAligned()
    {
        // 两条 assistant：第一条无推理（空串占位），第二条有推理——按顺序对齐。
        const string body = """
            {"model":"m","messages":[
                {"role":"assistant","content":"a"},
                {"role":"user","content":"next"},
                {"role":"assistant","content":"b"}
            ]}
            """;

        var patched = OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["", "第二条推理"]);

        patched.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patched!);
        var messages = doc.RootElement.GetProperty("messages");
        messages[0].TryGetProperty("reasoning_content", out _).Should().BeFalse();
        messages[2].GetProperty("reasoning_content").GetString().Should().Be("第二条推理");
    }

    [Fact]
    public void RewriteRequestBody_FewerSlotsThanAssistants_ExtraLeftUntouched()
    {
        const string body = """
            {"model":"m","messages":[
                {"role":"assistant","content":"a"},
                {"role":"assistant","content":"b"}
            ]}
            """;

        var patched = OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["只有一条"]);

        patched.Should().NotBeNull();
        using var doc = JsonDocument.Parse(patched!);
        var messages = doc.RootElement.GetProperty("messages");
        messages[0].GetProperty("reasoning_content").GetString().Should().Be("只有一条");
        messages[1].TryGetProperty("reasoning_content", out _).Should().BeFalse();
    }

    [Fact]
    public void RewriteRequestBody_NoReasoningSlots_ReturnsNull()
    {
        const string body = """{"model":"m","messages":[{"role":"assistant","content":"a"}]}""";

        OpenAiReasoningPassbackHandler.RewriteRequestBody(body, [""])
            .Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"model":"m"}""")]
    public void RewriteRequestBody_InvalidOrMessagelessBody_ReturnsNull(string body)
    {
        OpenAiReasoningPassbackHandler.RewriteRequestBody(body, ["r"]).Should().BeNull();
    }
}
