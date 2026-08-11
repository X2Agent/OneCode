using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Core.Permissions;
using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// PermissionChecker 异常 fail-closed 行为测试 — 验证当权限检查器自身抛异常时，
/// 中间件管道必须 fail-closed（拒绝执行工具），而不是 fail-open（放行工具调用）。
///
/// 覆盖的关键场景：
/// 1. PermissionChecker.CheckAsync 抛 InvalidOperationException → 中间件应阻止工具执行
/// 2. PermissionChecker.CheckAsync 抛 TimeoutException → 中间件应阻止工具执行
/// 3. 未知 PermissionMode 值时的 fail-closed fallback 行为
/// 4. ApprovalBroker 抛异常时的 fail-closed 行为
///
/// 这些测试防止安全关键的 fail-open 漏洞：如果权限检查器因任何原因失败，
/// 工具调用必须被拒绝，而非默认放行。
/// </summary>
public sealed class PermissionCheckerFailClosedTests
{
    private static readonly MethodInfo CheckPermissionMethod =
        typeof(PermissionAndLimitMiddleware).GetMethod(
            "CheckPermissionAndExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CheckPermissionAndExecuteAsync not found");

    private static Task<object> InvokeCheckPermissionAsync(
        AgentPipelineOptions options,
        FunctionInvocationContext ctx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object>> next,
        CancellationToken ct)
    {
        // 与 PermissionAndLimitMiddleware.Create 一致：预分配空集合
        var rulesBySource = options.RulesBySource ?? new Dictionary<string, PermissionRuleGroup>();
        var additionalWorkingDirectories = options.AdditionalWorkingDirectories
            ?? new Dictionary<string, AdditionalWorkingDirectory>();
        var sessionAllowlist = options.SessionAllowlist ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var boxed = CheckPermissionMethod.Invoke(
            null, new object[] { options, rulesBySource, additionalWorkingDirectories, sessionAllowlist, ctx, next, ct });
        return ((ValueTask<object>)boxed!).AsTask();
    }

    private static FunctionInvocationContext CreateContext(
        string toolName,
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

    private static AgentPipelineOptions BuildOptions(
        IPermissionChecker checker,
        PermissionMode mode = PermissionMode.Default,
        bool enableToolApproval = false) => new()
        {
            WorkingDirectory = "/test",
            PermissionChecker = checker,
            PermissionMode = mode,
            EnableToolApproval = enableToolApproval,
        };

    private sealed class FlagHolder
    {
        public bool Value;
    }

    // PermissionChecker.CheckAsync 抛异常时的 fail-closed 行为

    [Fact]
    public async Task CheckPermission_CheckerThrowsInvalidOperationException_FailsClosedWithoutExecuting()
    {
        // Arrange: PermissionChecker 抛 InvalidOperationException（模拟内部状态错误）
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PermissionCheckResult>(
                new InvalidOperationException("strategy lookup failed")));

        var ctx = CreateContext("Write");
        var options = BuildOptions(checker);
        var holder = new FlagHolder();

        // Act: 异常应传播，而非被吞掉后放行
        var act = () => InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        // Assert: 异常必须传播 — 中间件不能 catch 并默认放行
        await act.Should().ThrowAsync<InvalidOperationException>(
            "PermissionChecker 异常必须传播，不能被中间件吞掉后 fail-open");
        holder.Value.Should().BeFalse("工具绝不能在权限检查器异常时被执行");
    }

    [Fact]
    public async Task CheckPermission_CheckerThrowsTimeoutException_FailsClosedWithoutExecuting()
    {
        // Arrange: PermissionChecker 抛 TimeoutException（模拟策略超时）
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PermissionCheckResult>(
                new TimeoutException("permission service timed out")));

        var ctx = CreateContext("Bash");
        var options = BuildOptions(checker);
        var holder = new FlagHolder();

        // Act
        var act = () => InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        // Assert: 超时异常也必须传播，工具不能被执行
        await act.Should().ThrowAsync<TimeoutException>(
            "TimeoutException must propagate — no fail-open on timeout");
        holder.Value.Should().BeFalse("tool must not execute when permission check times out");
    }

    [Fact]
    public async Task CheckPermission_CheckerThrowsOperationCanceledException_PropagatesCancellation()
    {
        // Arrange: PermissionChecker 抛 OperationCanceledException（取消传播）
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PermissionCheckResult>(
                new OperationCanceledException(ct)));

        var ctx = CreateContext("Write");
        var options = BuildOptions(checker);
        var holder = new FlagHolder();

        // Act
        var act = () => InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        // Assert: 取消异常应传播（而非被转换为 fail-closed Deny）
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate distinctly from other exceptions");
        holder.Value.Should().BeFalse("tool must not execute when cancelled");
    }

    // 未知 PermissionMode 值时的 fail-closed fallback 行为

    [Fact]
    public async Task CheckAsync_UnregisteredMode_FallsBackToDefaultBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new PermissionChecker(CreateYoloClassifier());

        var ctx = new ToolPermissionContext
        {
            Mode = (PermissionMode)999,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var doc = JsonDocument.Parse(@"{""file_path"":""file.txt"",""content"":""x""}");

        var result = await checker.CheckAsync("Write", doc.RootElement, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Ask,
            "unknown permission mode must fall back to Default (Ask), not Allow (fail-open)");
    }

    // ApprovalBroker 抛异常时的 fail-closed 行为

    [Fact]
    public async Task CheckPermission_ApprovalBrokerThrows_PropagatesExceptionWithoutExecuting()
    {
        // Arrange: PermissionChecker 返回 Ask，ApprovalBroker 抛异常
        // 中间件不应吞掉异常后 fail-open 放行工具调用
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("confirm?"));

        var approvalBroker = Substitute.For<IApprovalBroker>();
        approvalBroker.RequestAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ApprovalDecision>(
                new InvalidOperationException("approval service unavailable")));

        var ctx = CreateContext("Write");
        var options = new AgentPipelineOptions
        {
            WorkingDirectory = "/test",
            PermissionChecker = checker,
            PermissionMode = PermissionMode.Default,
            ApprovalBroker = approvalBroker,
            EnableToolApproval = false, // 强制走 ApprovalBroker inline 路径
        };
        var holder = new FlagHolder();

        // Act
        var act = () => InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        // Assert: ApprovalBroker 异常必须传播，不能 fail-open 放行
        await act.Should().ThrowAsync<InvalidOperationException>(
            "ApprovalBroker exception must propagate — no fail-open when approval service fails");
        holder.Value.Should().BeFalse("tool must not execute when approval broker throws");
    }

    private static YoloClassifier CreateYoloClassifier()
    {
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        return new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null);
    }
}
