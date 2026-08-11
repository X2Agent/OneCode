using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;
using OneCode.Infrastructure.Config;

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
        registry.Register(MakeRegistration("h1", HookEvent.PreToolUse, "Bash"));
        var payload = MakePayload(HookEvent.PreToolUse, "Bash");

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
        registry.Register(MakeRegistration("h1", HookEvent.PreToolUse, "Bash"));
        var payload = MakePayload(HookEvent.PreToolUse, "Read");

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
        registry.Register(MakeRegistration("low-typed", HookEvent.PreToolUse, "Bash", priority: 200, type: HookType.Notification));
        registry.Register(MakeRegistration("high-typed", HookEvent.PreToolUse, "Bash", priority: 50, type: HookType.Command));
        var sut = CreateSutWith(registry, trusted: true, executors: [executorLow, executorHigh]);

        var result = await sut.FireAsync(MakePayload(HookEvent.PreToolUse, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        callOrder.Should().Equal(["high", "low"], "priority 升序：50 应先于 200 执行");
        result.AdditionalContexts.Should().NotBeNull();
        result.AdditionalContexts!.Should().ContainInOrder("ctx-high", "ctx-low");
    }

    // Once hook 自动注销

    [Fact]
    public async Task FireAsync_OnceHook_IsRemovedAfterExecution()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("ephemeral", HookEvent.PreToolUse, "Bash", once: true));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult { Message = "once" });
        var payload = MakePayload(HookEvent.PreToolUse, "Bash");

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

        registry.Register(MakeRegistration("thrower", HookEvent.PreToolUse, "Bash", priority: 50, type: HookType.Command));
        registry.Register(MakeRegistration("healthy", HookEvent.PreToolUse, "Bash", priority: 200, type: HookType.Notification));

        var sut2 = CreateSutWith(registry, trusted: true, executors: [throwing, healthy]);
        var result = await sut2.FireAsync(MakePayload(HookEvent.PreToolUse, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        // 异常被吞掉，healthy hook 仍执行
        await healthy.Received(1).ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>());
        result.AdditionalContexts.Should().ContainSingle(c => c == "healthy");
    }

    // 阻断结果聚合

    [Fact]
    public async Task FireAsync_BlockingResult_IsAggregatedIntoBlockingErrors()
    {
        var (sut, registry, executor) = CreateSut(trusted: true);
        registry.Register(MakeRegistration("blocker", HookEvent.PreToolUse, "Bash"));
        executor.ExecuteAsync(Arg.Any<HookPayload>(), Arg.Any<HookConfig>(), Arg.Any<CancellationToken>())
            .Returns(new HookResult
            {
                Outcome = HookOutcome.Blocking,
                BlockingError = new HookBlockingError("forbidden by policy", "cmd"),
            });

        var result = await sut.FireAsync(MakePayload(HookEvent.PreToolUse, "Bash"), actualMatcherValue: "Bash", ct: TestContext.Current.CancellationToken);

        result.BlockingErrors.Should().NotBeNull();
        result.BlockingErrors!.Should().ContainSingle(b => b.Error == "forbidden by policy");
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
        var commandExecutor = executors.FirstOrDefault(e => e.Type == HookType.Command)
            ?? executors.First();
        var notificationExecutor = executors.FirstOrDefault(e => e.Type == HookType.Notification)
            ?? Substitute.For<IHookExecutor>();
        var httpExecutor = executors.FirstOrDefault(e => e.Type == HookType.Http)
            ?? Substitute.For<IHookExecutor>();
        return new HookExecutionService(
            registry,
            commandExecutor,
            notificationExecutor,
            httpExecutor,
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
        HookEvent @event,
        string matcher,
        int priority = 100,
        bool once = false,
        HookType type = HookType.Command) => new()
        {
            Name = name,
            Event = @event,
            Matcher = matcher,
            Priority = priority,
            Once = once,
            ExecutorType = type,
            TimeoutMs = 5000,
            Config = new HookConfig(),
        };

    private static HookPayload MakePayload(HookEvent @event, string toolName) => new()
    {
        Event = @event,
        ToolName = toolName,
        SessionId = "test-session",
    };
}
