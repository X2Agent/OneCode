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

    [Fact]
    public async Task Replan_RenumbersFromMaxRetainedGoalId_AvoidingBothCollisionsAndNumberingGaps()
    {
        // 回归锚定：
        // 1) 按需递归分解用 Max(Id)+1 插入高位 Id（例如步骤 2 分解出 4, 5），replan 必须从保留段
        //    （[0..failedGoalIndex]）的最大 Id + 1 起算，防止与已执行的高位子步骤撞 Id；
        // 2) 未执行的剩余步骤（idx > failedGoalIndex）在重规划后会被丢弃，不得计入 baseId 计算，
        //    否则当步骤 #1 失败且步骤 #2, #3 尚未执行时，新步骤会跳号成 #4, #5（断层）。
        var responseText = """
            {"goals":[{"id":1,"description":"retry via refactor","successCriteria":"done"},
                      {"id":2,"description":"validate","successCriteria":"done"}]}
            """;
        var decomposer = CreateDecomposer(out var chatClient);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateResponse(responseText)));

        // 场景 A：初始 [1,2,3]，2 分解出 4,5 → plan=[1,2,4,5,3]，3（index 4）失败触发 replan。
        // 保留段 [1,2,4,5,3] 最大 Id 为 5，因此新步骤编号为 [6, 7]。
        var decomposedPlan = new GoalPlan
        {
            Goals =
            [
                new GoalItem { Id = 1, Description = "done step", SuccessCriteria = "done" },
                new GoalItem { Id = 2, Description = "decomposed parent", SuccessCriteria = "done" },
                new GoalItem { Id = 4, Description = "decomposed child a", SuccessCriteria = "done" },
                new GoalItem { Id = 5, Description = "decomposed child b", SuccessCriteria = "done" },
                new GoalItem { Id = 3, Description = "failed step", SuccessCriteria = "done" },
            ],
        };

        var decomposedResult = await decomposer.ReplanAsync(
            "original goal",
            decomposedPlan,
            failedGoalIndex: 4,
            [],
            modelId: null,
            TestContext.Current.CancellationToken);

        decomposedResult!.Value.RemainingGoals.Select(g => g.Id).Should().Equal(6, 7);

        // 场景 B：初始 [1,2,3]，步骤 #1（index 0）失败，未执行的 #2, #3 被丢弃。
        // 保留段仅 [#1]，最大 Id 为 1，因此新步骤必须连续编号为 [2, 3]，不得跳到 [4, 5]。
        var firstStepFailedPlan = new GoalPlan
        {
            Goals =
            [
                new GoalItem { Id = 1, Description = "failed step 1", SuccessCriteria = "done" },
                new GoalItem { Id = 2, Description = "unexecuted step 2", SuccessCriteria = "done" },
                new GoalItem { Id = 3, Description = "unexecuted step 3", SuccessCriteria = "done" },
            ],
        };

        var contiguousResult = await decomposer.ReplanAsync(
            "original goal",
            firstStepFailedPlan,
            failedGoalIndex: 0,
            [],
            modelId: null,
            TestContext.Current.CancellationToken);

        contiguousResult!.Value.RemainingGoals.Select(g => g.Id).Should().Equal(2, 3);
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
