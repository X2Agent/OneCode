using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Middleware.Contracts;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Permissions;
using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="PermissionAndLimitMiddleware"/>'s permission logic
/// (the private <c>CheckPermissionAndExecuteAsync</c> method).
///
/// The method is accessed via reflection because it is the single source of
/// truth for Allow/Deny/Ask/Passthrough/Bubble branching and is otherwise
/// only reachable through the full MAF pipeline (which requires a real
/// <see cref="AIAgent"/> instance).
/// </summary>
public sealed class AgentPipelineBuilderTests
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
        IPermissionChecker? checker = null,
        PermissionMode mode = PermissionMode.Default,
        string workingDirectory = "/test",
        Func<string, JsonElement, CancellationToken, Task<bool>>? approvalHandler = null,
        bool? enableToolApproval = null)
        => new()
        {
            WorkingDirectory = workingDirectory,
            PermissionChecker = checker,
            PermissionMode = mode,
            ApprovalHandler = approvalHandler,
            // PERM-1.5: 默认 true（与生产配置一致）；测试可显式覆盖以验证 Team inline 审批路径
            EnableToolApproval = enableToolApproval ?? true,
        };

    // Simple flag holder to track whether the next delegate was invoked.
    private sealed class FlagHolder
    {
        public bool Value;
    }

    // Test 1: PermissionChecker == null => execute tool directly (no check)
    [Fact]
    public async Task CheckPermission_NullChecker_ExecutesToolDirectlyWithoutCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = CreateContext("Bash");
        var options = BuildOptions(checker: null);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        holder.Value.Should().BeTrue("next should be invoked when PermissionChecker is null");
        result.Should().Be("tool-result");
    }

    // Test 2: PermissionChecker returns Allow => execute tool
    [Fact]
    public async Task CheckPermission_AllowDecision_ExecutesTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Allow);

        var ctx = CreateContext("Read");
        var options = BuildOptions(checker: checker);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        holder.Value.Should().BeTrue("Allow decision should invoke the tool");
        result.Should().Be("tool-result");
    }

    // Test 3: PermissionChecker returns Deny => returns deny message, tool not executed
    [Fact]
    public async Task CheckPermission_DenyDecision_ReturnsDenyMessageWithoutExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Deny("not allowed"));

        var ctx = CreateContext("Write");
        var options = BuildOptions(checker: checker);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("Deny decision should not invoke the tool");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Be("Tool 'Write' denied: not allowed");
    }

    // Test 4: PERM-1.5 — Ask + ApprovalHandler exists
    //         新行为：Ask → 放行到 next（ToolApprovalAgent 接管），ApprovalHandler 不被调用
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckPermission_AskDecision_PassesThroughToToolApprovalAgent(bool handlerApproved)
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("confirm?"));

        var approvalHandler = Substitute.For<Func<string, JsonElement, CancellationToken, Task<bool>>>();
        approvalHandler
            .Invoke(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(handlerApproved));

        var ctx = CreateContext("Write");
        var options = BuildOptions(checker: checker, approvalHandler: approvalHandler);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        // PERM-1.5: ApprovalHandler 不再被 PermissionAndLimitMiddleware 调用
        await approvalHandler.DidNotReceive().Invoke(
            Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        holder.Value.Should().BeTrue("Ask → 放行到 next（ToolApprovalAgent 接管）");
        result.Should().Be("tool-result");
    }

    // Test 5: PERM-1.5 — Passthrough → 放行到 next（与 Ask 行为一致）
    [Fact]
    public async Task CheckPermission_PassthroughDecision_PassesThroughToToolApprovalAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Passthrough("defer"));

        var approvalHandler = Substitute.For<Func<string, JsonElement, CancellationToken, Task<bool>>>();
        approvalHandler
            .Invoke(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var ctx = CreateContext("Edit");
        var options = BuildOptions(checker: checker, approvalHandler: approvalHandler);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        await approvalHandler.DidNotReceive().Invoke(
            Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        holder.Value.Should().BeTrue("Passthrough → 放行到 next（ToolApprovalAgent 接管）");
        result.Should().Be("tool-result");
    }

    // Test 6: PERM-1.5 — Ask + 无 ApprovalHandler → 放行到 next（不再 fail-safe Deny）
    // 新行为：Ask 路径由 MAF ToolApprovalAgent + AutoApprovalRules 接管
    [Fact]
    public async Task CheckPermission_AskDecision_WithoutApprovalHandler_PassesThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("confirm?"));

        var ctx = CreateContext("Write");
        var options = BuildOptions(checker: checker, approvalHandler: null);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        holder.Value.Should().BeTrue(
            "Ask → 放行到 next（ToolApprovalAgent + AutoApprovalRules 接管，不再 inline Deny）");
        result.Should().Be("tool-result");
    }

    // Test 7: EnableToolApproval:false + Ask + ApprovalHandler 批准
    //         Team 路径：ApprovalHandler inline 处理，批准则放行
    [Fact]
    public async Task CheckPermission_AskDecision_WithApprovalDisabled_AndHandlerApproves_InvokesHandlerInline()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("confirm?"));

        var approvalHandler = Substitute.For<Func<string, JsonElement, CancellationToken, Task<bool>>>();
        approvalHandler
            .Invoke(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var ctx = CreateContext("Write");
        var options = BuildOptions(
            checker: checker,
            approvalHandler: approvalHandler,
            enableToolApproval: false);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        await approvalHandler.Received(1).Invoke(
            Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        holder.Value.Should().BeTrue("ApprovalHandler inline 批准后应放行到 next");
        result.Should().Be("tool-result");
    }

    // Test 8: EnableToolApproval:false + Ask + ApprovalHandler 拒绝
    //         返回 ToolResult.Error，next 不被调用
    [Fact]
    public async Task CheckPermission_AskDecision_WithApprovalDisabled_AndHandlerDenies_ReturnsDeny()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("confirm?"));

        var approvalHandler = Substitute.For<Func<string, JsonElement, CancellationToken, Task<bool>>>();
        approvalHandler
            .Invoke(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var ctx = CreateContext("Write");
        var options = BuildOptions(
            checker: checker,
            approvalHandler: approvalHandler,
            enableToolApproval: false);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        await approvalHandler.Received(1).Invoke(
            Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        holder.Value.Should().BeFalse("ApprovalHandler 拒绝后不应调用 next");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Contain("denied by user");
    }

    // Test 9: PERM-1.5 fail-safe — EnableToolApproval:false + Ask + 无 ApprovalHandler
    //         无任何审批通道时 fail-safe Deny（防止 fail-open）
    [Theory]
    [InlineData(PermissionDecision.Ask)]
    [InlineData(PermissionDecision.Passthrough)]
    public async Task CheckPermission_WithApprovalDisabled_AndNoHandler_FailSafeDeny(
        PermissionDecision decision)
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = Substitute.For<IPermissionChecker>();
        var checkResult = decision switch
        {
            PermissionDecision.Ask => PermissionCheckResult.Ask("confirm?"),
            PermissionDecision.Passthrough => PermissionCheckResult.Passthrough("defer"),
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(checkResult);

        var ctx = CreateContext("Write");
        var options = BuildOptions(
            checker: checker,
            approvalHandler: null,
            enableToolApproval: false);
        var holder = new FlagHolder();

        var result = await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => { holder.Value = true; return new ValueTask<object>("tool-result"); },
            ct);

        holder.Value.Should().BeFalse("fail-safe Deny 不应调用 next");
        result.Should().BeOfType<ToolResult>()
            .Which.Content.Should().Contain("requires approval but no approval channel is available");
    }

    // Test 10: PermissionMode and WorkingDirectory correctly passed to ToolPermissionContext
    [Fact]
    public async Task CheckPermission_PassesModeAndWorkingDirectoryToPermissionContext()
    {
        var ct = TestContext.Current.CancellationToken;
        ToolPermissionContext? captured = null;

        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Do<ToolPermissionContext>(ctx => captured = ctx),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Allow);

        var ctx = CreateContext("Read");
        var options = BuildOptions(
            checker: checker,
            mode: PermissionMode.Plan,
            workingDirectory: "C:/custom/session/dir");

        await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => new ValueTask<object>("ok"), ct);

        captured.Should().NotBeNull();
        captured!.Mode.Should().Be(PermissionMode.Plan,
            "PermissionMode from AgentPipelineOptions should flow into ToolPermissionContext.Mode");
        captured.WorkingDirectory.Should().Be("C:/custom/session/dir",
            "WorkingDirectory from AgentPipelineOptions should flow into ToolPermissionContext.WorkingDirectory");
    }

    [Fact]
    public async Task CheckPermission_PassesRulesAndAdditionalDirsToPermissionContext()
    {
        var ct = TestContext.Current.CancellationToken;
        ToolPermissionContext? captured = null;

        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(
                Arg.Any<string>(),
                Arg.Any<JsonElement>(),
                Arg.Do<ToolPermissionContext>(ctx => captured = ctx),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Allow);

        var rules = new Dictionary<string, PermissionRuleGroup>
        {
            ["project"] = new PermissionRuleGroup(
                AlwaysAllow: [new PermissionRule("Bash", "git status")]),
        };
        var additionalDirs = new Dictionary<string, AdditionalWorkingDirectory>
        {
            ["extra"] = new AdditionalWorkingDirectory("/extra", WorkingDirectorySource.AddDirCommand),
        };
        var allowlist = new HashSet<string> { "Read" };

        var ctx = CreateContext("Bash");
        var options = new AgentPipelineOptions
        {
            WorkingDirectory = "/work",
            PermissionChecker = checker,
            RulesBySource = rules,
            AdditionalWorkingDirectories = additionalDirs,
            SessionAllowlist = allowlist,
        };

        await InvokeCheckPermissionAsync(
            options, ctx,
            (_, _) => new ValueTask<object>("ok"), ct);

        captured.Should().NotBeNull();
        captured!.RulesBySource.Should().BeSameAs(rules);
        captured.AdditionalWorkingDirectories.Should().BeSameAs(additionalDirs);
        captured.SessionAllowlist.Should().BeEquivalentTo(allowlist);
    }

    // PIPE-1.6: Pipeline factory contract tests
    // 同一 PipelineSecurityContext，三种 role 产出的 options 在安全字段上等价；
    // 仅工具集与配额按 role 裁剪。危险路径（/add-dir 外写入、alwaysDeny）行为一致。

    private static YoloClassifier CreateYoloClassifier()
    {
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        return new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null);
    }

    private static PipelineSecurityContext BuildSecurityContext(
        PermissionMode mode = PermissionMode.Default,
        IVerificationProvider? verificationProvider = null,
        IPermissionChecker? permissionChecker = null,
        bool enableVerification = false,
        EditTransaction? transaction = null) => new(
        WorkingDirectory: "/work",
        PermissionMode: mode,
        RulesBySource: new Dictionary<string, PermissionRuleGroup>
        {
            ["project"] = new PermissionRuleGroup(
                AlwaysDeny: [new PermissionRule("Bash", "rm *")]),
        },
        AdditionalWorkingDirectories: new Dictionary<string, AdditionalWorkingDirectory>
        {
            ["extra"] = new AdditionalWorkingDirectory("/extra", WorkingDirectorySource.AddDirCommand),
        },
        SessionAllowlist: new HashSet<string> { "Read" },
        Hook: Substitute.For<IHookExecutionService>(),
        VerificationProvider: verificationProvider,
        EnableVerification: enableVerification,
        OrchestrationEventSink: null,
        FileChangeCallback: null,
        ModelId: "test-model",
        ProviderId: "test-provider",
        SafetyInvariants: new[] { Substitute.For<ISafetyInvariant>() },
        BehaviorContracts: [new FileEditContract("/tmp")],
        EditTransaction: transaction ?? new EditTransaction(),
        PermissionChecker: permissionChecker);

    [Fact]
    public void PipelineFactory_WorkerHasSameSafetyFieldsAsMain()
    {
        // 契约：同一 PipelineSecurityContext，三种 role 产出的 options 在安全字段上等价
        var ctx = BuildSecurityContext();

        var main = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main limit"));
        var worker = AgentPipelineOptionsFactory.CreateForWorker(ctx, maxToolCalls: 10);
        var team = AgentPipelineOptionsFactory.Create(
            PipelineProfile.TeamMember, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 20, ToolLimitMessage: "team limit"));

        // 共享安全字段（所有 role 等价，引用相同）
        main.WorkingDirectory.Should().Be(worker.WorkingDirectory);
        main.WorkingDirectory.Should().Be(team.WorkingDirectory);

        main.RulesBySource.Should().BeSameAs(worker.RulesBySource);
        main.RulesBySource.Should().BeSameAs(team.RulesBySource);

        main.AdditionalWorkingDirectories.Should().BeSameAs(worker.AdditionalWorkingDirectories);
        main.AdditionalWorkingDirectories.Should().BeSameAs(team.AdditionalWorkingDirectories);

        main.SessionAllowlist.Should().BeSameAs(worker.SessionAllowlist);
        main.SessionAllowlist.Should().BeSameAs(team.SessionAllowlist);

        main.SafetyInvariants.Should().BeSameAs(worker.SafetyInvariants);
        main.SafetyInvariants.Should().BeSameAs(team.SafetyInvariants);

        main.BehaviorContracts.Should().BeSameAs(worker.BehaviorContracts);
        main.BehaviorContracts.Should().BeSameAs(team.BehaviorContracts);

        main.EditTransaction.Should().BeSameAs(worker.EditTransaction);
        main.EditTransaction.Should().BeSameAs(team.EditTransaction);

        main.PermissionChecker.Should().BeSameAs(worker.PermissionChecker);
        main.PermissionChecker.Should().BeSameAs(team.PermissionChecker);

        main.HookExecutionService.Should().BeSameAs(worker.HookExecutionService);
        main.HookExecutionService.Should().BeSameAs(team.HookExecutionService);

        main.VerificationProvider.Should().BeSameAs(worker.VerificationProvider);
        main.VerificationProvider.Should().BeSameAs(team.VerificationProvider);

        // 角色裁剪字段差异（工具集与配额，按 role 裁剪）
        main.MaxToolCalls.Should().Be(50);
        worker.MaxToolCalls.Should().Be(10);
        team.MaxToolCalls.Should().Be(20);

        main.ToolLimitMessage.Should().Be("main limit");
        worker.ToolLimitMessage.Should().Contain("10");
        team.ToolLimitMessage.Should().Be("team limit");
    }

    [Fact]
    public void AddDir_WorkerRespectsAdditionalDirectories()
    {
        // 契约：/add-dir 添加的目录在 Worker/Team 与 Main 一致传递
        var ctx = BuildSecurityContext();
        var main = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));
        var worker = AgentPipelineOptionsFactory.CreateForWorker(ctx, maxToolCalls: 10);
        var team = AgentPipelineOptionsFactory.Create(
            PipelineProfile.TeamMember, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 20, ToolLimitMessage: "team"));

        worker.AdditionalWorkingDirectories.Should().NotBeNull();
        worker.AdditionalWorkingDirectories.Should().ContainKey("extra");
        worker.AdditionalWorkingDirectories!["extra"].Path.Should().Be("/extra");
        worker.AdditionalWorkingDirectories.Should().BeSameAs(main.AdditionalWorkingDirectories);
        team.AdditionalWorkingDirectories.Should().BeSameAs(main.AdditionalWorkingDirectories);
    }

    [Fact]
    public async Task AlwaysDeny_WorkerRespectsAsMain()
    {
        // 契约：alwaysDeny 规则在 Worker/Team 与 Main 行为一致（通过 PermissionChecker 实际调用验证）
        var checker = new PermissionChecker(CreateYoloClassifier());
        var ctx = BuildSecurityContext(
            mode: PermissionMode.Default,
            permissionChecker: checker);

        var main = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));
        var worker = AgentPipelineOptionsFactory.CreateForWorker(ctx, maxToolCalls: 10);

        // PermissionChecker 与 RulesBySource 在两条路径上一致
        main.PermissionChecker.Should().BeSameAs(worker.PermissionChecker);
        main.RulesBySource.Should().BeSameAs(worker.RulesBySource);

        // 实际调用 PermissionChecker：alwaysDeny "Bash rm *" 在 Main/Worker 上下文中都应 Deny
        using var inputDoc = JsonDocument.Parse(@"{""command"":""rm -rf /""}");
        var input = inputDoc.RootElement;
        var ct = TestContext.Current.CancellationToken;

        var mainResult = await checker.CheckAsync("Bash", input,
            new ToolPermissionContext
            {
                Mode = main.PermissionMode,
                WorkingDirectory = main.WorkingDirectory,
                RulesBySource = main.RulesBySource,
                AdditionalWorkingDirectories = main.AdditionalWorkingDirectories,
                SessionAllowlist = main.SessionAllowlist,
            }, ct);
        var workerResult = await checker.CheckAsync("Bash", input,
            new ToolPermissionContext
            {
                Mode = worker.PermissionMode,
                WorkingDirectory = worker.WorkingDirectory,
                RulesBySource = worker.RulesBySource,
                AdditionalWorkingDirectories = worker.AdditionalWorkingDirectories,
                SessionAllowlist = worker.SessionAllowlist,
            }, ct);

        mainResult.Decision.Should().Be(PermissionDecision.Deny, "Main 路径应拒绝 rm *");
        workerResult.Decision.Should().Be(PermissionDecision.Deny, "Worker 路径应同等拒绝 rm *");
    }

    [Fact]
    public void PipelineFactory_VerificationFollowsProfile()
    {
        // PIPE-1.7: Verification 默认跟随 Profile
        var verificationProvider = Substitute.For<IVerificationProvider>();

        // Auto 模式：Profile.EnableVerification=true 应覆盖 ctx.EnableVerification=false
        var autoCtx = BuildSecurityContext(
            mode: PermissionMode.Auto,
            verificationProvider: verificationProvider,
            enableVerification: false);
        var autoOptions = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, autoCtx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));
        autoOptions.EnableVerification.Should().BeTrue(
            "Auto Profile.EnableVerification=true 应覆盖 ctx.EnableVerification=false");

        // Worker 路径同样跟随 Profile
        var autoWorker = AgentPipelineOptionsFactory.CreateForWorker(autoCtx, maxToolCalls: 10);
        autoWorker.EnableVerification.Should().BeTrue(
            "Worker 路径也应跟随 Auto Profile.EnableVerification=true");

        // Default 模式：Profile.EnableVerification=false 应覆盖 ctx.EnableVerification=true
        var defaultCtx = BuildSecurityContext(
            mode: PermissionMode.Default,
            verificationProvider: verificationProvider,
            enableVerification: true);
        var defaultOptions = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, defaultCtx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));
        defaultOptions.EnableVerification.Should().BeFalse(
            "Default Profile.EnableVerification=false 应覆盖 ctx.EnableVerification=true");
    }

    [Fact]
    public void PipelineFactory_VerificationUsesCtxWhenNoProvider()
    {
        var ctx = BuildSecurityContext(enableVerification: true);

        var options = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full, ctx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));

        options.EnableVerification.Should().BeTrue(
            "without VerificationProvider, ctx.EnableVerification is used");
    }

    [Fact]
    public void FullProfile_EnablesTaskRecoveryAndBehaviorContracts()
    {
        var ctx = BuildSecurityContext();
        var full = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Full,
            ctx,
            new PipelineRoleOverrides(MaxToolCalls: 50, ToolLimitMessage: "main"));
        var worker = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Worker,
            ctx,
            new PipelineRoleOverrides(MaxToolCalls: 10, ToolLimitMessage: "worker"));

        full.EnableTaskRecovery.Should().BeTrue();
        full.EnableBehaviorContracts.Should().BeTrue();
        worker.EnableTaskRecovery.Should().BeFalse();
        worker.EnableBehaviorContracts.Should().BeTrue();
    }

    [Fact]
    public void ExploreProfile_DisablesVerificationAndContracts()
    {
        var ctx = BuildSecurityContext(enableVerification: true);
        var explore = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Explore,
            ctx,
            new PipelineRoleOverrides(MaxToolCalls: 10, ToolLimitMessage: "explore"));

        explore.EnableVerification.Should().BeFalse();
        explore.EnableBehaviorContracts.Should().BeFalse();
        explore.BehaviorContracts.Should().BeNull();
    }
}
