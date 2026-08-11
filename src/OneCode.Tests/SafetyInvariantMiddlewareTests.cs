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
}
