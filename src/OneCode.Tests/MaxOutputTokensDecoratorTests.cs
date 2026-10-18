using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

public sealed class MaxOutputTokensDecoratorTests
{
    [Fact]
    public async Task GetResponseAsync_UnsetMaxOutputTokensWithoutReasoning_AppliesEscalatedDefaultWithoutMutatingCallerOptions()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { FinishReason = ChatFinishReason.Stop });
        var sut = new MaxOutputTokensDecorator(inner);
        var callerOptions = new ChatOptions { ModelId = "step-5-preview" };

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "decompose")],
            callerOptions,
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("ok");
        callerOptions.MaxOutputTokens.Should().BeNull("decorator must clone ChatOptions rather than mutating caller state");
        await inner.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.MaxOutputTokens == MaxOutputTokensDecorator.EscalatedMaxTokens),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResponseAsync_ReasoningEnabledAndMaxOutputTokensUnset_KeepsNullUntilLengthHit()
    {
        // 开启显式 Reasoning 时首轮必须保持 MaxOutputTokens = null（避免 Anthropic 适配器将 budget_tokens 压扁到 max_tokens - 1）；
        // 若首轮仍触发 Length 截断，装饰器再升级到 EscalatedMaxTokens 重试。
        var inner = Substitute.For<IChatClient>();
        var callCount = 0;
        var seenCaps = new List<int?>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenCaps.Add(ci.Arg<ChatOptions?>()?.MaxOutputTokens);
                return ++callCount == 1
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.Length }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "VERDICT: DONE")) { FinishReason = ChatFinishReason.Stop };
            });
        var sut = new MaxOutputTokensDecorator(inner);
        var callerOptions = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        };

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "judge")],
            callerOptions,
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("VERDICT: DONE");
        seenCaps.Should().Equal([null, MaxOutputTokensDecorator.EscalatedMaxTokens],
            "first call with Reasoning must keep MaxOutputTokens null, and Length finish reason must escalate on retry");
        callerOptions.MaxOutputTokens.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitSmallCapHitsLength_EscalatesToEscalatedMaxTokensAndRecovers()
    {
        var inner = Substitute.For<IChatClient>();
        var seenCaps = new List<int?>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var cap = ci.Arg<ChatOptions?>()?.MaxOutputTokens;
                seenCaps.Add(cap);
                return cap < MaxOutputTokensDecorator.EscalatedMaxTokens
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.Length }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"goals":[]}""")) { FinishReason = ChatFinishReason.Stop };
            });
        var sut = new MaxOutputTokensDecorator(inner);
        var callerOptions = new ChatOptions { MaxOutputTokens = 2_048 };

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "plan")],
            callerOptions,
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("""{"goals":[]}""");
        seenCaps.Should().Equal([2_048, MaxOutputTokensDecorator.EscalatedMaxTokens]);
        callerOptions.MaxOutputTokens.Should().Be(2_048, "caller's original ChatOptions must not be mutated");
    }

    [Fact]
    public async Task GetResponseAsync_EscalatedCapStillHitsLength_ContinuesWithEscalatedMaxTokensInsteadOfResettingToNull()
    {
        var inner = Substitute.For<IChatClient>();
        var callCount = 0;
        var seenCaps = new List<int?>();
        var seenMessageCounts = new List<int>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenCaps.Add(ci.Arg<ChatOptions?>()?.MaxOutputTokens);
                seenMessageCounts.Add(ci.Arg<IEnumerable<ChatMessage>>().Count());
                return ++callCount == 1
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial part 1")) { FinishReason = ChatFinishReason.Length }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed part 2")) { FinishReason = ChatFinishReason.Stop };
            });
        var sut = new MaxOutputTokensDecorator(inner);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "write long output")],
            cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().Be("completed part 2");
        seenCaps.Should().Equal(
            [MaxOutputTokensDecorator.EscalatedMaxTokens, MaxOutputTokensDecorator.EscalatedMaxTokens],
            "continuation turns must keep EscalatedMaxTokens rather than resetting MaxOutputTokens to null");
        seenMessageCounts.Should().Equal([1, 3],
            "continuation must append the partial assistant turn and recovery prompt without a redundant pre-continuation retry");
    }

    [Fact]
    public async Task GetResponseAsync_SmallContextWindow_ClampsEscalationCapToContextWindowMinusOne()
    {
        var inner = Substitute.For<IChatClient>();
        var callCount = 0;
        var seenCaps = new List<int?>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenCaps.Add(ci.Arg<ChatOptions?>()?.MaxOutputTokens);
                return ++callCount == 1
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "part 1")) { FinishReason = ChatFinishReason.Length }
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "part 2")) { FinishReason = ChatFinishReason.Stop };
            });
        var sut = new MaxOutputTokensDecorator(inner, contextWindow: 8_192);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ollama small window")],
            new ChatOptions { MaxOutputTokens = 2_048 },
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("part 2");
        seenCaps.Should().Equal([2_048, 8_191],
            "small contextWindow must clamp escalation cap to contextWindow - 1 instead of 65536");
    }
}
