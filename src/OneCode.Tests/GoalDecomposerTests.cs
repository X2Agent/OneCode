using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

public sealed class GoalDecomposerTests
{
    [Theory]
    [InlineData("{\"goals\":[]}", "{\"goals\":[]}")]
    [InlineData("```json\n{\"goals\":[]}\n```", "{\"goals\":[]}")]
    [InlineData("Here is the plan:\n{\"goals\":[]}\nhope it helps", "{\"goals\":[]}")]
    [InlineData("prefix {\"a\":\"{ not depth }\"} suffix", "{\"a\":\"{ not depth }\"}")]
    [InlineData("{\"a\":\"quote \\\" brace }\"}", "{\"a\":\"quote \\\" brace }\"}")]
    [InlineData("no json here", null)]
    [InlineData("{\"unclosed\":", null)]
    [InlineData("", null)]
    public void ExtractJsonBlock_HandlesFencesStringsAndPlainText(string input, string? expected)
        => GoalDecomposer.ExtractJsonBlock(input).Should().Be(expected);

    [Fact]
    public async Task Decompose_FencedJsonOutput_ParsesWithoutFallback()
    {
        var responseText = """
            ```json
            {"goals":[{"id":1,"description":"do the thing","successCriteria":"done"}]}
            ```
            """;
        var decomposer = CreateDecomposer(out var chatClient);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateResponse(responseText)));

        var result = await decomposer.DecomposeWithFallbackAsync("goal", null, TestContext.Current.CancellationToken);

        result.UsedFallback.Should().BeFalse();
        result.Error.Should().BeNull();
        result.Plan.Goals.Should().ContainSingle().Which.Description.Should().Be("do the thing");
        result.InputTokens.Should().Be(4);
        result.OutputTokens.Should().Be(2);
    }

    [Fact]
    public async Task Decompose_PlainJsonWithoutFence_ParsesWithoutFallback()
    {
        const string responseText = "{\"goals\":[{\"id\":1,\"description\":\"plain\",\"successCriteria\":\"done\"}]}";
        var decomposer = CreateDecomposer(out var chatClient);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateResponse(responseText)));

        var result = await decomposer.DecomposeWithFallbackAsync("goal", null, TestContext.Current.CancellationToken);

        result.UsedFallback.Should().BeFalse();
        result.Plan.Goals.Should().ContainSingle().Which.Description.Should().Be("plain");
    }

    [Fact]
    public async Task Decompose_RequestsJsonSchemaResponseFormat()
    {
        var decomposer = CreateDecomposer(out var chatClient);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(
                CreateResponse("""{"goals":[{"id":1,"description":"x","successCriteria":"y"}]}""")));

        await decomposer.DecomposeWithFallbackAsync("goal", null, TestContext.Current.CancellationToken);

        // 结构化请求优先：发往模型的 ChatOptions 必须携带 JSON schema 的 ResponseFormat
        _ = chatClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.ResponseFormat != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decompose_ResponseFormatRejected_RetriesWithoutFormatAndParses()
    {
        var decomposer = CreateDecomposer(out var chatClient);
        var callCount = 0;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ++callCount == 1
                ? Task.FromException<ChatResponse>(
                    new InvalidOperationException("400: response_format is not supported"))
                : Task.FromResult(CreateResponse(
                    """
                    ```json
                    {"goals":[{"id":1,"description":"retried","successCriteria":"done"}]}
                    ```
                    """)));

        var result = await decomposer.DecomposeWithFallbackAsync("goal", null, TestContext.Current.CancellationToken);

        // ③ 硬拒绝后无格式重试，重试的围栏输出由 ExtractJsonBlock 降级解析成功，不走单目标回退
        result.UsedFallback.Should().BeFalse();
        result.Plan.Goals.Should().ContainSingle().Which.Description.Should().Be("retried");

        _ = chatClient.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        // 第二次调用必须去掉 ResponseFormat，回到 prompt-only 基线
        _ = chatClient.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o == null || o.ResponseFormat == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decompose_CancelledLlmCall_PropagatesCancellation()
    {
        var decomposer = CreateDecomposer(out var chatClient);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(callInfo => throw new OperationCanceledException());

        var act = () => decomposer.DecomposeWithFallbackAsync("goal", null, TestContext.Current.CancellationToken);

        // H3: 取消必须向上传播，而不是被当成"分解失败"吞掉走回退。
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ChatResponse CreateResponse(string text) => new(
        new ChatMessage(ChatRole.Assistant, text))
    {
        Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2 },
    };

    private static GoalDecomposer CreateDecomposer(out IChatClient chatClient)
    {
        chatClient = Substitute.For<IChatClient>();
        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate("system/goal-decomposer", "json contract"));
        return new GoalDecomposer(
            chatClient,
            NullLogger<GoalDecomposer>.Instance,
            promptManager);
    }
}
