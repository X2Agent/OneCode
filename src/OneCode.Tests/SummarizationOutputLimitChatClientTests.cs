using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Compact;
using OneCode.Core.Domain;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>
/// A3 行为契约：两条压缩路径必须给出同等的摘要输出上限。
/// </summary>
/// <remarks>
/// MAF 的 <c>SummarizationCompactionStrategy</c> 调用聊天客户端时不传 <c>ChatOptions</c>，
/// 因此自动路径的输出长度完全依赖提供方默认值；显式 <c>/compact</c> 则传了 8192。
/// 同一段对话因触发路径不同而产出不同规模的摘要，属于契约分叉。
/// </remarks>
public sealed class SummarizationOutputLimitChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_NoCallerOptions_AppliesProductLimit()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        var sut = new SummarizationOutputLimitChatClient(inner, SummarizationDefaults.MaxOutputTokens);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "compact this")]);

        await inner.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.MaxOutputTokens == SummarizationDefaults.MaxOutputTokens),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 反证：调用方显式给出的上限优先，装饰器不得覆盖。
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_CallerSpecifiedLimit_IsNotOverridden()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        var sut = new SummarizationOutputLimitChatClient(inner, SummarizationDefaults.MaxOutputTokens);

        await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "compact this")],
            new ChatOptions { MaxOutputTokens = 512 });

        await inner.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.MaxOutputTokens == 512),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 调用方传入的 ChatOptions 实例不得被就地修改——调用方可能复用同一实例。
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_DoesNotMutateCallerOptions()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        var sut = new SummarizationOutputLimitChatClient(inner, SummarizationDefaults.MaxOutputTokens);
        var callerOptions = new ChatOptions();

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], callerOptions);

        callerOptions.MaxOutputTokens.Should().BeNull("the decorator must clone before filling in a default");
    }

    /// <summary>
    /// 装饰器必须把内层客户端自身的服务透传给调用方。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 手写 <see cref="IChatClient"/> 实现时最容易漏掉的就是 <c>GetService</c>，漏掉后调用方会
    /// <b>静默</b>拿不到提供方自身的服务（遥测钩子、具体实现等），表现为功能悄悄消失而不是报错。
    /// 改用 <see cref="DelegatingChatClient"/> 后由基类转发，本用例锁住这个行为。
    /// </para>
    /// <para>
    /// 探测类型必须是<b>装饰器自己不是其实例</b>的类型：装饰器对
    /// <c>serviceType.IsInstanceOfType(this)</c> 成立时会返回自身（这是正确的装饰器语义），
    /// 用 <c>typeof(object)</c> 断言会误判。
    /// </para>
    /// </remarks>
    [Fact]
    public void GetService_IsForwardedToInnerClient()
    {
        var inner = Substitute.For<IChatClient>();
        var marker = new ChatClientMetadata("probe-provider");
        inner.GetService(typeof(ChatClientMetadata), null).Returns(marker);
        var sut = new SummarizationOutputLimitChatClient(inner, SummarizationDefaults.MaxOutputTokens);

        sut.GetService(typeof(ChatClientMetadata), null).Should().BeSameAs(marker,
            "a decorator that swallows GetService hides the provider's own services from callers");
    }

    /// <summary>流式路径同样要套上默认上限，否则自动路径的流式摘要会漏掉限额。</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_NoCallerOptions_AppliesProductLimit()
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(EmptyUpdates());
        var sut = new SummarizationOutputLimitChatClient(inner, SummarizationDefaults.MaxOutputTokens);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
        {
        }

        inner.Received(1).GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.MaxOutputTokens == SummarizationDefaults.MaxOutputTokens),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A3 契约：显式 <c>/compact</c> 与 in-pipeline 摘要必须绑定到同一个输出上限常量。
    /// </summary>
    /// <remarks>
    /// 两侧各自写死数值时，任何一侧被单独修改都会让「同一段对话、不同触发路径、不同摘要规模」
    /// 的分叉重新出现，而且不会让任何单侧用例失败——本用例把两侧绑到同一常量上。
    /// in-pipeline 一侧由本类的 <c>AppliesProductLimit</c> 用例以同一常量断言。
    /// </remarks>
    [Fact]
    public void ExplicitCompactPath_UsesTheSharedSummarizationOutputLimit()
    {
        var builder = new CompactPromptBuilder(Substitute.For<IPromptManager>());

        var request = builder.BuildChatRequest(
            [new UserMessage(Id: "u1", Content: "hello", Timestamp: DateTimeOffset.UtcNow)],
            systemPrompt: "summarise",
            model: "test-model");

        request.Options.MaxOutputTokens.Should().Be(SummarizationDefaults.MaxOutputTokens,
            "the explicit /compact path and the MAF in-pipeline strategy must bound summaries identically");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyUpdates()
    {
        await Task.CompletedTask;
        yield break;
    }
}
