using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// SafetyInvariantMiddleware fail-closed 契约测试。
///
/// 验证两条核心安全不变量：
/// 1. invariant 检测到恶意参数返回 Deny 时，中间件 fail-closed（不调用 next，返回 ToolResult.Error）
/// 2. SafetyInvariantMiddleware 独立于 PermissionMode — 即使 BypassPermissions 模式，
///    Layer 0 检查仍执行并阻止恶意操作（中间件签名不接收 PermissionMode 参数，体现独立性）
/// </summary>
public sealed class SafetyInvariantMiddlewareTests
{
    private static FunctionInvocationContext CreateContext(
        string toolName = "Bash",
        IDictionary<string, object?>? args = null)
    {
        var function = Substitute.For<AIFunction>();
        function.Name.Returns(toolName);
        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(
                (args ?? new Dictionary<string, object?>())
                    .ToDictionary(kv => kv.Key, kv => kv.Value)),
        };
    }

    private sealed class FlagHolder
    {
        public bool Value;
    }

    // FailClosedOnMalformedArguments
    // invariant 检测到恶意参数 → Deny → 中间件 fail-closed
    [Fact]
    public async Task FailClosedOnMalformedArguments_InvariantDenies_ReturnsErrorWithoutExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Deny("malicious arguments detected"));

        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);
        var ctx = CreateContext("Bash", new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("invariant Deny 时不应调用 next（fail-closed）");
        result.Should().BeOfType<ToolResult>()
            .Which.IsError.Should().BeTrue("返回的 ToolResult 必须标记为错误");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Contain("malicious arguments detected");
    }

    // BypassPermissionsStillBlocksMalformed
    // SafetyInvariantMiddleware 签名不接收 PermissionMode — Layer 0 检查独立于权限模式。
    // 此测试验证：即使调用方处于 BypassPermissions 上下文，invariant 仍被调用且 Deny 时 fail-closed。
    [Fact]
    public async Task BypassPermissionsStillBlocksMalformed_InvariantIndependentOfPermissionMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Deny("blocked regardless of permission mode"));

        // 关键：SafetyInvariantMiddleware.Create 不接收 PermissionMode 参数，
        // 体现 Layer 0 独立于权限模式 — BypassPermissions 也无法绕过。
        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);
        var ctx = CreateContext("Write", new Dictionary<string, object?> { ["filePath"] = "/etc/passwd" });
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("BypassPermissions 不影响 SafetyInvariant — 仍 fail-closed");
        result.Should().BeOfType<ToolResult>()
            .Which.IsError.Should().BeTrue();

        // 验证 invariant 确实被调用（Layer 0 检查执行了，而非被权限模式跳过）
        await invariant.Received(1).CheckAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    // 多个 invariant 链式检查 — 第一个 Deny 即 fail-closed，短路后续 invariant
    [Fact]
    public async Task MultipleInvariants_FirstDenies_StopsChainAndReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant1 = Substitute.For<ISafetyInvariant>();
        invariant1.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Deny("first invariant denied"));

        var invariant2 = Substitute.For<ISafetyInvariant>();
        invariant2.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Allow);

        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant1, invariant2 }, NullLogger.Instance);
        var ctx = CreateContext("Bash");
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("第一个 invariant Deny 应 fail-closed");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Contain("first invariant denied");

        // 短路：第二个 invariant 不应被调用
        await invariant2.DidNotReceive().CheckAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    // 所有 invariant Allow → 工具正常执行（happy path）
    [Fact]
    public async Task AllInvariantsAllow_ExecutesTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Allow);

        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);
        var ctx = CreateContext("Read");
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeTrue("所有 invariant Allow 时应执行工具");
        result.Should().Be("tool-result");
    }

    // next 抛出 ArgumentException（AIFunctionFactory 参数绑定失败，如 LLM 调用工具时
    // 缺少必需参数）→ 中间件兜底返回 ToolResult.Error 而不是让异常穿透中间件链
    // 崩溃整个 agent run。LLM 收到错误后可修正参数重试。
    [Fact]
    public async Task NextThrowsArgumentException_ReturnsToolErrorInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Allow);

        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);
        var ctx = CreateContext("SubmitPlan");

        var act = async () => await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => throw new ArgumentException(
                "The arguments dictionary is missing a value for the required parameter 'content'. (Parameter 'arguments')"),
            ct);

        var result = await act();
        result.Should().BeOfType<ToolResult>()
            .Which.IsError.Should().BeTrue("参数绑定失败应转为可重试的工具错误");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Contain("SubmitPlan")
            .And.Contain("content",
                "错误消息应包含工具名和缺失的参数名，指导 LLM 修正重试");
    }

    // 非 ArgumentException（如工具内部逻辑错误）不在此层兜底——保持原有异常传播契约
    [Fact]
    public async Task NextThrowsInvalidOperationException_StillPropagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Allow);

        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);
        var ctx = CreateContext("Read");

        var act = () => middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => throw new InvalidOperationException("unexpected"),
            ct).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "仅兜底参数绑定（ArgumentException）；其他异常保持原有传播语义");
    }

    // 工具调用的 JSON 参数在模型侧解析失败（CallContent.Exception 非空，参数为空字典）：
    // 典型原因是单次输出过长被截断。必须返回可指导自愈的错误（分段写入策略），
    // 而不是 [SAFETY] fail-closed 消息误导模型原样重发同样的大参数。
    [Fact]
    public async Task CallContentParseException_ReturnsTruncationHintWithoutExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);

        var function = AIFunctionFactory.Create((string filePath, string content) => "ok", name: "Write");
        var ctx = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            CallContent = new FunctionCallContent("call-1", "Write")
            {
                Exception = new InvalidOperationException("Error parsing function call arguments."),
            },
        };
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("参数解析失败时不得执行工具");
        result.Should().BeOfType<ToolResult>().Which.IsError.Should().BeTrue();
        result.Should().BeOfType<ToolResult>().Which.Content.Should()
            .Contain("truncated", "错误必须指出截断这一可能原因")
            .And.Contain("Edit", "错误必须给出分段写入的自愈路径");
        await invariant.DidNotReceive().CheckAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    // 参数为空（无 CallContent 异常）但工具声明了必需参数：空参数不可能是合法调用，
    // 同样返回截断/自愈提示，而不是让工具或 invariant 层报出难以理解的错误。
    [Fact]
    public async Task EmptyArgumentsForToolWithRequiredParameters_ReturnsTruncationHint()
    {
        var ct = TestContext.Current.CancellationToken;
        var middleware = SafetyInvariantMiddleware.Create(
            [], NullLogger.Instance);

        var function = AIFunctionFactory.Create((string filePath, string content) => "ok", name: "Write");
        var ctx = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            CallContent = new FunctionCallContent("call-2", "Write"),
        };
        var holder = new FlagHolder();

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object?>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("声明了必需参数的工具收到空参数时不得执行");
        result.Should().BeOfType<ToolResult>().Which.Content.Should()
            .Contain("truncated").And.Contain("Edit");
    }

    // 反证：无必需参数的工具收到空参数是合法调用，不得被截断守卫拦截。
    [Fact]
    public async Task EmptyArgumentsForToolWithoutRequiredParameters_StillExecutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var invariant = Substitute.For<ISafetyInvariant>();
        invariant.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(InvariantCheckResult.Allow);
        var middleware = SafetyInvariantMiddleware.Create(
            new[] { invariant }, NullLogger.Instance);

        // 所有参数都有默认值 → schema 的 required 为空，空参数调用合法。
        var function = AIFunctionFactory.Create((string? path = null) => "ok", name: "Sleep");
        var ctx = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            CallContent = new FunctionCallContent("call-3", "Sleep"),
        };

        var result = await middleware(
            Substitute.For<AIAgent>(), ctx,
            (_, _) => new ValueTask<object?>("tool-result"),
            ct);

        result.Should().Be("tool-result", "无必需参数的工具空参调用不应被拦截");
    }
}
