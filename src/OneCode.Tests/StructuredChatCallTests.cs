using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services;

namespace OneCode.Tests;

/// <summary>
/// "结构化请求 + 文本降级"辅助的三段式契约：
/// ①schema 请求成功 → 类型化结果；②忽略 schema/带围栏 → 同响应文本降级不重试；
/// ③硬拒绝 → 无 ResponseFormat 重试一次；取消传播且不重试。
/// </summary>
public sealed class StructuredChatCallTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // GetResponseAsync<T>（结构化路径）内部会 MakeReadOnly()，未显式指定解析器的实例会在那里抛异常，
        // 且异常会被 StructuredChatCall 的拒绝重试宽捕获吞掉、伪装成网关拒绝。见 StructuredChatCall 的守卫。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private sealed record Sample(string Name, int Age);

    private static Task<ChatResponse> Response(string text) => Task.FromResult(
        new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    private static IChatClient CreateClient(
        Func<int, Task<ChatResponse>> responseForCall)
    {
        var chat = Substitute.For<IChatClient>();
        var callCount = 0;
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => responseForCall(++callCount));
        return chat;
    }

    private static Task<StructuredChatResult<Sample>> CallAsync(
        IChatClient chat, CancellationToken ct) =>
        StructuredChatCall.CallAsync<Sample>(
            chat,
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions(),
            SerializerOptions,
            logger: null,
            ct);

    [Fact]
    public async Task CleanJson_ReturnsTypedValue_AndRequestsJsonSchema()
    {
        var chat = CreateClient(call => Response("""{"name":"ada","age":36}"""));

        var result = await CallAsync(chat, TestContext.Current.CancellationToken);

        result.Value.Should().Be(new Sample("ada", 36));
        result.ViaSchema.Should().BeTrue();
        result.Text.Should().Be("""{"name":"ada","age":36}""");

        // ① 首次请求必须携带 JSON schema 的 ResponseFormat，且只有一次调用
        _ = chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.ResponseFormat != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FencedJson_FallsBackToSameResponseText_WithoutRetry()
    {
        var chat = CreateClient(call => Response("""
            ```json
            {"name":"grace","age":45}
            ```
            """));

        var result = await CallAsync(chat, TestContext.Current.CancellationToken);

        // ② schema 被忽略或输出带围栏：不重试，把原样文本交给调用方降级解析器
        result.Value.Should().BeNull();
        result.ViaSchema.Should().BeFalse();
        result.Text.Should().Contain("```json");

        _ = chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResponseFormatRejected_RetriesWithoutFormat_AndParsesRetryText()
    {
        var chat = CreateClient(call => call switch
        {
            1 => Task.FromException<ChatResponse>(
                new InvalidOperationException("400: response_format is not supported")),
            _ => Response("""{"name":"linus","age":54}"""),
        });

        var result = await CallAsync(chat, TestContext.Current.CancellationToken);

        // ③ 网关硬拒绝：去掉 ResponseFormat 重试，重试文本可直接类型化
        result.Value.Should().Be(new Sample("linus", 54));
        result.ViaSchema.Should().BeFalse();

        _ = chat.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
        _ = chat.Received().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o == null || o.ResponseFormat == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectionRetry_FencedText_LeavesParsingToCaller()
    {
        var chat = CreateClient(call => call switch
        {
            1 => Task.FromException<ChatResponse>(
                new InvalidOperationException("400: response_format is not supported")),
            _ => Response("""
                ```json
                {"name":"fenced","age":1}
                ```
                """),
        });

        var result = await CallAsync(chat, TestContext.Current.CancellationToken);

        result.Value.Should().BeNull();
        result.Text.Should().Contain("```json");
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutRetry()
    {
        var chat = CreateClient(call =>
            Task.FromException<ChatResponse>(new OperationCanceledException()));

        var act = () => CallAsync(chat, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();

        _ = chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTypeInfoResolver_FailsFast_InsteadOfMasqueradingAsRejection()
    {
        var chat = Substitute.For<IChatClient>();

        // 未指定 TypeInfoResolver 的实例会让 GetResponseAsync<T> 内部的 MakeReadOnly() 抛异常；
        // 守卫必须在该异常被③宽捕获吞掉之前失败，否则 bug 会伪装成"网关拒绝"并静默降级。
        var act = () => StructuredChatCall.CallAsync<Sample>(
            chat,
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            logger: null,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        chat.ReceivedCalls().Should().BeEmpty();
    }
}
