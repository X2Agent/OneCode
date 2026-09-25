using OneCode.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookExecutionService 主链路单测——覆盖：
///   工作区不可信跳过
///   → matcher 过滤
///   → 优先级排序
///   → 串行执行 + 结果聚合
///   → Once hook 自动注销
///   → 执行器异常隔离
/// </summary>
/// <remarks>
/// 此测试类通过静态 <c>Cwd</c> 字段捕获一次 <see cref="Directory.GetCurrentDirectory"/>
/// 并将其标记为受信任目录；而 <see cref="HookPolicyService.IsCurrentWorkspaceTrusted"/>
/// 在运行时再次读取 cwd。因此本类对进程级 cwd 敏感，必须与其它修改 cwd 的测试类
/// 串行执行，否则 cwd 被其它测试改写后信任校验失败、hooks 不执行、断言空集合。
/// </remarks>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class HookExecutionServiceTests
{
    private static readonly string Cwd = Path.GetFullPath(Directory.GetCurrentDirectory());

    // 策略前置检查

    [Fact]
    public async Task FireAsync_WorkspaceNotTrusted_ReturnsEmptyAndDoesNotInvokeExecutors()
    {
        var (sut, registry, executor) = CreateSut(trusted: false);
        registry.Register(MakeRegistration("h1", HookInterceptionPoint.PreToolCall, "Bash"));
        var payload = MakePayload(HookInterceptionPoint.PreToolCall, "Bash");

        var result = await sut.FireAsync(payload, actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.BlockingErrors.Should().BeNull();
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>());
    }

    // matcher 过滤

    [Fact]
    public async Task FireAsync_NoMatchingHooks_ReturnsEmpty()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("h1", HookInterceptionPoint.PreToolCall, "Bash"));
        var payload = MakePayload(HookInterceptionPoint.PreToolCall, "Read");

        var result = await sut.FireAsync(payload, actualMatcherValue: "Read", ct: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.BlockingErrors.Should().BeNull();
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>());
    }

    // 优先级排序 + 串行执行 + 聚合

    [Fact]
    public async Task FireAsync_MultipleHooks_ExecutesInPriorityOrderAndAggregates()
    {
        var callOrder = new List<string>();
        var executorLow = Substitute.For<IHookExecutor>();
        executorLow.Type.Returns(HookType.Notification);
        executorLow.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("low"); return new HookResult { AdditionalContext = "ctx-low" }; });

        var executorHigh = Substitute.For<IHookExecutor>();
        executorHigh.Type.Returns(HookType.Command);
        executorHigh.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("high"); return new HookResult { AdditionalContext = "ctx-high" }; });

        var registry = new HookRegistry(new GlobHookMatcher());
        registry.Register(MakeRegistration("low-typed", HookInterceptionPoint.PreToolCall, "Bash", priority: 200, type: HookType.Notification));
        registry.Register(MakeRegistration("high-typed", HookInterceptionPoint.PreToolCall, "Bash", priority: 50, type: HookType.Command));
        var sut = CreateSutWith(registry, trusted: true, executors: [executorLow, executorHigh]);

        var result = await sut.FireAsync(MakePayload(HookInterceptionPoint.PreToolCall, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        callOrder.Should().Equal(["high", "low"], "priority 升序：50 应先于 200 执行");
        result.AdditionalContexts.Should().NotBeNull();
        result.AdditionalContexts!.Should().ContainInOrder("ctx-high", "ctx-low");
    }

    // Once hook 自动注销

    [Fact]
    public async Task FireAsync_OnceHook_IsRemovedAfterExecution()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("ephemeral", HookInterceptionPoint.PreToolCall, "Bash", once: true));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult { Message = "once" });
        var payload = MakePayload(HookInterceptionPoint.PreToolCall, "Bash");

        await sut.FireAsync(payload, actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);
        registry.GetAll().Should().BeEmpty("Once hook 执行后应自动注销");

        // 二次 fire 不应再触发 executor
        executor.ClearReceivedCalls();
        await sut.FireAsync(payload, actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);
        await executor.DidNotReceive().ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>());
    }

    // 执行器异常隔离

    [Fact]
    public async Task FireAsync_ExecutorThrows_IsSwallowedAndOtherHooksStillRun()
    {
        var (_, registry, _) = CreateSut(trusted: true);
        var throwing = Substitute.For<IHookExecutor>();
        throwing.Type.Returns(HookType.Command);
        throwing.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var healthy = Substitute.For<IHookExecutor>();
        healthy.Type.Returns(HookType.Notification);
        healthy.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult { AdditionalContext = "healthy" });

        registry.Register(MakeRegistration("thrower", HookInterceptionPoint.PreToolCall, "Bash", priority: 50, type: HookType.Command));
        registry.Register(MakeRegistration("healthy", HookInterceptionPoint.PreToolCall, "Bash", priority: 200, type: HookType.Notification));

        var sut2 = CreateSutWith(registry, trusted: true, executors: [throwing, healthy]);
        var result = await sut2.FireAsync(MakePayload(HookInterceptionPoint.PreToolCall, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        // 异常被吞掉，healthy hook 仍执行
        await healthy.Received(1).ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>());
        result.AdditionalContexts.Should().ContainSingle(c => c == "healthy");
    }

    // 阻断结果聚合

    [Fact]
    public async Task FireAsync_BlockingResult_IsAggregatedIntoBlockingErrors()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("blocker", HookInterceptionPoint.PreToolCall, "Bash"));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult
            {
                Outcome = HookOutcome.Blocking,
                BlockingError = new HookBlockingError("forbidden by policy", "cmd"),
            });

        var result = await sut.FireAsync(MakePayload(HookInterceptionPoint.PreToolCall, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        result.BlockingErrors.Should().NotBeNull();
        result.BlockingErrors!.Should().ContainSingle(b => b.Error == "forbidden by policy");
    }

    // HasActiveHooks 与 FireAsync 前置过滤同源

    [Fact]
    public void HasActiveHooks_MatchingHookInTrustedWorkspace_ReturnsTrue()
    {
        var (sut, registry, _) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("h1", HookInterceptionPoint.PreModelCall, "gpt-*"));

        sut.HasActiveHooks(HookInterceptionPoint.PreModelCall, "gpt-5").Should().BeTrue();
    }

    [Fact]
    public void HasActiveHooks_NoMatchingHook_ReturnsFalse()
    {
        var (sut, registry, _) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("h1", HookInterceptionPoint.PreModelCall, "gpt-*"));

        sut.HasActiveHooks(HookInterceptionPoint.PreModelCall, "claude-4").Should().BeFalse();
        sut.HasActiveHooks(HookInterceptionPoint.PostModelCall, "gpt-5").Should().BeFalse();
    }

    [Fact]
    public void HasActiveHooks_WorkspaceNotTrusted_ReturnsFalse()
    {
        var (sut, registry, _) = CreateSut(trusted: false);
        registry.Register(MakeRegistration("h1", HookInterceptionPoint.PreModelCall, "gpt-*"));

        sut.HasActiveHooks(HookInterceptionPoint.PreModelCall, "gpt-5").Should().BeFalse(
            "工作区不受信任时 FireAsync 会跳过执行，HasActiveHooks 必须同源返回 false");
    }

    // Once 语义：仅在成功执行后注销（A4 修复）

    [Fact]
    public async Task FireAsync_OnceHookWithBlockingResult_IsRemovedAfterExecution()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("once-blocker", HookInterceptionPoint.Output, "Completed", once: true));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult
            {
                Outcome = HookOutcome.Blocking,
                BlockingError = new HookBlockingError("must continue", "hook"),
            });

        var result = await sut.FireAsync(MakePayload(HookInterceptionPoint.Output, ""), actualMatcherValue: "Completed", ct: TestContext.Current.CancellationToken);

        result.BlockingErrors.Should().NotBeNull();
        registry.GetAll().Should().BeEmpty("Once hook 送达阻断裁决后应注销，防止同一拦截点无限循环触发");
    }

    [Fact]
    public async Task FireAsync_OnceHookWithExecutorException_IsKeptForRetry()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("once-flaky", HookInterceptionPoint.Output, "Completed", once: true));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        await sut.FireAsync(MakePayload(HookInterceptionPoint.Output, ""), actualMatcherValue: "Completed", ct: TestContext.Current.CancellationToken);

        registry.GetAll().Should().ContainSingle("执行异常视为未完成，once hook 应保留待下次触发");
    }
    // helpers

    private static (HookExecutionService sut, HookRegistry registry, IHookExecutor executor) CreateSut(
        bool trusted)
    {
        var registry = new HookRegistry(new GlobHookMatcher());
        var executor = Substitute.For<IHookExecutor>();
        executor.Type.Returns(HookType.Command);
        var sut = CreateSutWith(registry, trusted, executor);
        return (sut, registry, executor);
    }

    private static HookExecutionService CreateSutWith(
        HookRegistry registry,
        bool trusted,
        params IHookExecutor[] executors)
    {
        var config = CreateConfigManager(trusted);
        var policy = new HookPolicyService(config);
        return new HookExecutionService(
            registry,
            executors,
            policy,
            NullLogger<HookExecutionService>.Instance);
    }

    private static IConfigManager CreateConfigManager(bool trusted)
    {
        var values = new Dictionary<string, object?>();
        if (trusted)
            values["trustedDirectories"] = new List<string> { Cwd };

        var settings = new AppSettings(values);
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(settings));
        return config;
    }

    private static HookRegistration MakeRegistration(
        string name,
        HookInterceptionPoint point,
        string matcher,
        int priority = 100,
        bool once = false,
        HookType type = HookType.Command) => new()
        {
            Name = name,
            Point = point,
            Matcher = matcher,
            Priority = priority,
            Once = once,
            ExecutorType = type,
            TimeoutMs = 5000,
            Config = new HookConfig(),
        };

    private static HookPayload MakePayload(HookInterceptionPoint point, string toolName) => new()
    {
        Point = point,
        ToolName = toolName,
        SessionId = "test-session",
    };
}
