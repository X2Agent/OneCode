using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ToolArgumentBindingMiddleware"/> 测试：验证按 schema 归一化畸形数组参数、
/// 编组失败转可读错误。next 使用真实 <see cref="AIFunctionFactory"/> 函数走完反射编组，
/// 确保归一化后的参数确实能被编组接受（而非仅形状断言）。
/// </summary>
public sealed class ToolArgumentBindingMiddlewareTests
{
    private static readonly AIAgent Agent = Substitute.For<AIAgent>();

    private readonly ToolArgumentBindingMiddleware _middleware = new(NullLogger.Instance);

    [Fact]
    public async Task CreateDelegate_StringArgumentForArrayParameter_WrapsIntoSingleElementArray()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        var context = CreateContext(function, """{"items":"alpha,beta"}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("alpha,beta", "无法解析的字符串按单元素数组兜底后应走完真实编组");
    }

    [Fact]
    public async Task CreateDelegate_StringifiedArrayArgument_IsDecodedToRealArray()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        var context = CreateContext(function, """{"items":"[\"a\",\"b\"]"}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("a|b", "数组被二次编码成 JSON 字符串时应解码一层再编组");
    }

    [Fact]
    public async Task CreateDelegate_AlreadyArrayArgument_PassesThroughUnchanged()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        var context = CreateContext(function, """{"items":["a","b"]}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("a|b");
    }

    [Fact]
    public async Task CreateDelegate_NonArrayParameter_PassesThroughUnchanged()
    {
        var function = AIFunctionFactory.Create(SampleTools.Echo, name: "Echo");
        var context = CreateContext(function, """{"prompt":"hi"}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("hi");
    }

    [Fact]
    public async Task CreateDelegate_NullableArraySchema_StringArgument_WrapsIntoSingleElementArray()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinTags, name: "JoinTags");
        var context = CreateContext(function, """{"tags":"x,y"}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("x,y", "type 为 [array,null] 的可空数组参数同样应被归一化");
    }

    [Fact]
    public async Task CreateDelegate_ObjectArgumentForItemObjectArray_WrapsIntoSingleElementArray()
    {
        var function = AIFunctionFactory.Create(SampleTools.FirstNode, name: "FirstNode");
        var context = CreateContext(function, """{"nodes":{"x":2}}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Contain("\"x\"", "items 未声明类型的 JsonElement[] 应接受单对象包装");
    }

    [Fact]
    public async Task CreateDelegate_StringArgumentForObjectArray_ReturnsReadableError()
    {
        var invoked = false;
        var function = AIFunctionFactory.Create(SampleTools.TakeItems, name: "TakeItems");
        var context = CreateContext(function, """{"items":"name"}""");

        var result = await RunAsync(context, (_, _) =>
        {
            invoked = true;
            return new ValueTask<object?>((object?)"invoked");
        });

        var toolResult = result.Should().BeOfType<ToolResult>().Subject;
        toolResult.IsError.Should().BeTrue("string 无法编组为对象数组，应提前返回错误");
        toolResult.Content.Should().Contain("items");
        toolResult.Content.Should().Contain("array of object");
        invoked.Should().BeFalse("错误路径不得调用底层工具函数");
    }

    [Fact]
    public async Task CreateDelegate_ObjectArgumentForStringArray_ReturnsReadableError()
    {
        var invoked = false;
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        var context = CreateContext(function, """{"items":{"a":1}}""");

        var result = await RunAsync(context, (_, _) =>
        {
            invoked = true;
            return new ValueTask<object?>((object?)"invoked");
        });

        var toolResult = result.Should().BeOfType<ToolResult>().Subject;
        toolResult.IsError.Should().BeTrue();
        toolResult.Content.Should().Contain("items");
        toolResult.Content.Should().Contain("JSON object");
        invoked.Should().BeFalse("错误路径不得调用底层工具函数");
    }

    [Fact]
    public async Task CreateDelegate_NestedMarshallingFailure_ReturnsErrorInsteadOfThrowing()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        // 解码后的数组元素是数字，string[] 编组仍会抛 JsonException——必须被兜底转成错误结果。
        // 若异常仍会抛出，本用例将以异常失败（等价于 NotThrow 断言）。
        var context = CreateContext(function, """{"items":"[1,2]"}""");

        var result = await RunAsync(context);

        var toolResult = result.Should().BeOfType<ToolResult>().Subject;
        toolResult.IsError.Should().BeTrue();
        toolResult.Content.Should().Contain("binding failed");
    }

    [Fact]
    public async Task CreateDelegate_CaseVariantArgumentKey_IsStillNormalized()
    {
        var function = AIFunctionFactory.Create(SampleTools.JoinItems, name: "Join");
        var captured = new Dictionary<string, object?>();
        var context = CreateContext(function, """{"Items":"alpha"}""");

        await _middleware.CreateDelegate()(
            Agent,
            context,
            (ctx, _) =>
            {
                foreach (var pair in ctx.Arguments)
                    captured[pair.Key] = pair.Value;
                return new ValueTask<object?>((object?)"skipped");
            },
            TestContext.Current.CancellationToken);

        var normalized = captured["Items"].Should().BeOfType<JsonElement>().Subject;
        normalized.ValueKind.Should().Be(JsonValueKind.Array, "大小写不同的参数键也应被按 schema 匹配并归一化");
        normalized.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task CreateDelegate_FunctionMissing_PassesThrough()
    {
        var invoked = false;
        var context = new FunctionInvocationContext
        {
            Arguments = new AIFunctionArguments(),
        };

        var result = await _middleware.CreateDelegate()(
            Agent,
            context,
            (_, _) =>
            {
                invoked = true;
                return new ValueTask<object?>((object?)"passthrough");
            },
            TestContext.Current.CancellationToken);

        result.Should().Be("passthrough");
        invoked.Should().BeTrue();
    }

    [Fact]
    public async Task CreateDelegate_SnakeCaseAndStringifiedScalarArguments_AreNormalizedAndBoundToCamelCaseParameters()
    {
        var function = AIFunctionFactory.Create(SampleTools.EditSlice, name: "Edit");
        var context = CreateContext(
            function,
            """{"file_path":"src/App.cs","old_string":"foo","new_string":"bar","replace_all":"true","line_offset":"12"}""");

        var result = await RunAsync(context);

        ResultText(result).Should().Be("src/App.cs:foo->bar:replaceAll=True:offset=12",
            "snake_case 键名与字符串形式的 bool/int 标量应被归一化并成功绑定到 C# camelCase 参数");
    }

    [Fact]
    public async Task CreateDelegate_CommonAliasKeys_MapToCanonicalSchemaPropertyWithoutOverwritingExplicitCanonicalKey()
    {
        var editFn = AIFunctionFactory.Create(SampleTools.EditSlice, name: "Edit");
        var bashFn = AIFunctionFactory.Create(SampleTools.RunCommand, name: "Bash");

        var viaAlias = await RunAsync(CreateContext(
            editFn,
            """{"path":"a.cs","oldString":"x","newString":"y"}"""));
        var explicitWins = await RunAsync(CreateContext(
            editFn,
            """{"filePath":"canonical.cs","path":"ignored.cs","oldString":"x","newString":"y"}"""));
        var cmdAlias = await RunAsync(CreateContext(
            bashFn,
            """{"cmd":"dotnet test"}"""));

        ResultText(viaAlias).Should().Be("a.cs:x->y:replaceAll=False:offset=0");
        ResultText(explicitWins).Should().Be("canonical.cs:x->y:replaceAll=False:offset=0",
            "显式传入标准参数名时不得被同名别名覆盖");
        ResultText(cmdAlias).Should().Be("ran:dotnet test");
    }

    private async Task<object?> RunAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? nextOverride = null)
    {
        return await _middleware.CreateDelegate()(
            Agent,
            context,
            nextOverride ?? ((ctx, token) => new ValueTask<object?>(
                ctx.Function.InvokeAsync(ctx.Arguments, token).AsTask())),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static FunctionInvocationContext CreateContext(AIFunction function, string toolCallJson)
    {
        using var document = JsonDocument.Parse(toolCallJson);
        var arguments = new AIFunctionArguments();
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();

        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = arguments,
        };
    }

    /// <summary>MEAI 10.x 把函数返回值编组为 JsonElement；还原成可读文本以便断言。</summary>
    private static string ResultText(object? result) => result switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        string text => text,
        _ => result?.ToString() ?? "null",
    };

    private sealed class SampleTools
    {
        public static string JoinItems(string[] items) => string.Join("|", items);

        public static string Echo(string prompt) => prompt;

        public static string JoinTags(string[]? tags) => string.Join("|", tags ?? []);

        public static string FirstNode(JsonElement[] nodes) => nodes[0].GetRawText();

        public static string TakeItems(SampleItem[] items) => string.Join("|", items.Select(item => item.Name));

        public static string EditSlice(
            string filePath,
            string oldString,
            string newString,
            bool replaceAll = false,
            int lineOffset = 0)
            => $"{filePath}:{oldString}->{newString}:replaceAll={replaceAll}:offset={lineOffset}";

        public static string RunCommand(string command) => $"ran:{command}";
    }

    internal sealed record SampleItem(string Name);
}
