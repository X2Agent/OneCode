using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Core.Config;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tokens;
using CoreConstants = OneCode.Core.Constants;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Api;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="ForkedAgentRunner"/>.
/// </summary>
public sealed class ForkedAgentRunnerTests
{

    private static AgentContextPipeline CreateContextPipeline(
        IPermissionModeProvider modeProvider)
    {
        var (shared, main) = TestSupport.TestAgentContextProviderAssembly.Create(
            modeProvider: modeProvider);
        return new AgentContextPipeline(shared, main);
    }


    private static ForkedAgentRunner CreateRunner(
        OneCode.Core.Config.IConfigManager? configManager = null,
        WorkingModeController? modeController = null)
    {
        configManager ??= TestConfigManager.Create();
        var modeProvider = new PermissionModeProvider(configManager);
        var pipelineAssembly = new AgentPipelineAssembly(
            Substitute.For<IWorkingDirectoryAccessor>(),
            Substitute.For<IHookExecutionService>(),
            Substitute.For<IVerificationProvider>(),
            modeProvider,
            Substitute.For<IPermissionChecker>(),
            new TokenLedger());

        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness for forks."));

        return new ForkedAgentRunner(
            logger: NullLogger<ForkedAgentRunner>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            serviceProvider: Substitute.For<IServiceProvider>(),
            contextPipeline: CreateContextPipeline(modeProvider),
            pipelineFactory: new SubAgentPipelineFactory(
                modeProvider,
                Substitute.For<IHookExecutionService>(),
                Substitute.For<IVerificationProvider>(),
                Substitute.For<IPermissionChecker>(),
                Substitute.For<IAppStateAccessor>(),
                new TokenLedger(),
                configManager),
            modeController: modeController ?? new WorkingModeController(),
            runtime: new ForkedAgentRuntimeDependencies(
                null!,
                Substitute.For<IModelManager>(),
                Substitute.For<IWorkingDirectoryAccessor>(),
                new ToolMetadataRegistry(),
                new CompactionStrategyFactory(
                    null!,
                    Substitute.For<IModelManager>(),
                    new OneCode.App.Services.Compact.CompactPromptBuilder(new PromptManager())),
                new SessionIdHolder(),
                configManager),
            promptComposer: new PromptComposer(promptManager));
    }
    [Fact]
    public void BuildOptions_PropagatesConversationIdToPipelineOptions()
    {
        // fork 管道的 usage 归属：ConversationId 经 SubAgentPipelineFactory → AgentPipelineOptions
        // 直达 UsageTrackingRunMiddleware（会话级 /status 统计的入账前提）；
        // 上下文提供器不消费该字段（子代理转录保持 fork 本地）。
        var configManager = TestConfigManager.Create();
        var modeProvider = new PermissionModeProvider(configManager);
        var factory = new SubAgentPipelineFactory(
            modeProvider,
            Substitute.For<IHookExecutionService>(),
            Substitute.For<IVerificationProvider>(),
            Substitute.For<IPermissionChecker>(),
            Substitute.For<IAppStateAccessor>(),
            new TokenLedger(),
            configManager);

        var sessionId = SessionId.NewId();
        var options = factory.BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.Worker,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            MaxToolCalls = 10,
            ConversationId = sessionId,
        });

        options.ConversationId.Should().Be(sessionId);
    }

    [Fact]
    public void WorkerSharedProviders_AreReleasableByLease()
    {
        // fork 不经过主代理管道的租约，必须自己 Track/Dispose。这里验证前提：
        // Worker 的 shared providers 里确实有每 run 新建且可释放的实例（skills）。
        var modeProvider = new PermissionModeProvider(TestConfigManager.Create());
        var pipeline = CreateContextPipeline(modeProvider);

        var providers = pipeline.BuildShared(
            PipelineProfile.Worker,
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() });

        using var lease = AgentContextProviderLease.Track(providers);

        lease.Count.Should().BeGreaterThan(0,
            "Worker 每 run 构建 skills provider；若这里为 0，说明能力集或工厂接线变了，fork 的释放逻辑会失效");
    }

    [Fact]
    public void BuildContextProviderOptions_CarriesControllerWorkingMode_NotNull()
    {
        // Plan 模式的 fork worker 必须把模式带给共享 provider：CodeAct provider 在
        // 装配期按 WorkingMode 自检剔除 execute_code。此前该字段恒为 null，
        // Plan 模式下子代理仍带 execute_code，模式约束只剩权限层事后拒绝。
        var runner = CreateRunner(modeController: new WorkingModeController(WorkingMode.Plan));

        var options = runner.BuildContextProviderOptions(
            new ForkedAgentParams(), Path.GetTempPath());

        options.WorkingMode.Should().Be(WorkingMode.Plan,
            "the fork must inherit the active mode so assembly-time capability exclusion applies");
        options.WorkingDirectory.Should().Be(Path.GetTempPath());
    }

    [Fact]
    public void BuildOptions_BuildParent_SnapshotsLedgerAsBudgetBaseline()
    {
        // BUILD 父级按 run 基线计量（主路径语义），fork 镜像同一规则：以 fork 启动
        // 时点 ledger 快照为基线——长 Plan 阶段的会话累计不再让子代理开跑即触预算硬停。
        var ledger = new TokenLedger();
        // 可计费 = (1000-800) 非缓存输入 + 200 输出 + 100 缓存写 + 800×10% 缓存读 = 580
        ledger.RecordUsage(new UsageRecord("test-model", 1000, 200, 800, 100));

        var options = CreateFactory(ledger).BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.Worker,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            ParentWorkingMode = WorkingMode.Build,
            MaxToolCalls = 10,
        });

        options.BudgetBaselineTokens.Should().Be(580,
            "a BUILD fork measures only its own delta, not the whole session's accumulated tokens");
    }

    [Theory]
    [InlineData(WorkingMode.Plan)]
    [InlineData(WorkingMode.Goal)]
    public void BuildOptions_NonBuildParent_KeepsSessionAccumulatedBaseline(WorkingMode parentMode)
    {
        // PLAN/GOAL 主路径维持会话级累计（基线 0），fork 与主路径同口径。
        var ledger = new TokenLedger();
        ledger.RecordUsage(new UsageRecord("test-model", 1000, 200, 800, 100));

        var options = CreateFactory(ledger).BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.Worker,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            ParentWorkingMode = parentMode,
            MaxToolCalls = 10,
        });

        options.BudgetBaselineTokens.Should().Be(0,
            "PLAN/GOAL measure budget against the session-accumulated total, mirroring the main path");
    }

    [Fact]
    public void BuildOptions_TurnBudgetNudge_EnabledOnlyWhenMaxTurnsPresent()
    {
        // fork 管道带轮数预算时开启 80% 收尾提醒；MaxTurns 缺省（GOAL DAG 等路径）
        // 没有可参照的轮数预算，开关不得打开。
        var withTurns = CreateFactory(new TokenLedger()).BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.Explore,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            MaxToolCalls = 10,
            MaxTurns = 15,
        });
        var withoutTurns = CreateFactory(new TokenLedger()).BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.Explore,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            MaxToolCalls = 10,
        });

        withTurns.EnableTurnBudgetNudge.Should().BeTrue("带轮数预算的 fork 管道须开启收尾提醒");
        withoutTurns.EnableTurnBudgetNudge.Should().BeFalse();
    }

    private static SubAgentPipelineFactory CreateFactory(TokenLedger ledger)
    {
        var configManager = TestConfigManager.Create();
        var modeProvider = new PermissionModeProvider(configManager);
        return new SubAgentPipelineFactory(
            modeProvider,
            Substitute.For<IHookExecutionService>(),
            Substitute.For<IVerificationProvider>(),
            Substitute.For<IPermissionChecker>(),
            Substitute.For<IAppStateAccessor>(),
            ledger,
            configManager);
    }

    [Fact]
    public void FilterChildTools_StripsDispatchTools_KeepsOthers()
    {
        // 嵌套派工深度上限 1：子代理工具面一律摘除派工工具，其余工具保留
        var tools = ForkedAgentRunner.FilterChildTools(
            [Tool("Agent"), Tool("ParallelAgents"), Tool("Read"), Tool("Grep")],
            capabilities: null);

        tools.Select(t => t.Name).Should().BeEquivalentTo("Read", "Grep");
    }

    [Fact]
    public void FilterChildTools_IntersectsWithAllowedToolNames()
    {
        var capabilities = ToolCapabilitySet.CreateUnrestricted(["Read"]);

        var tools = ForkedAgentRunner.FilterChildTools(
            [Tool("Agent"), Tool("Read"), Tool("Grep")],
            capabilities);

        tools.Select(t => t.Name).Should().BeEquivalentTo("Read");
    }

    [Fact]
    public void FilterChildTools_NullSource_YieldsEmpty()
        => ForkedAgentRunner.FilterChildTools(null, null).Should().BeEmpty();

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(static () => "", name: name);

    [Fact]
    public async Task RunForkedAgentAsync_NullChatClient_ReturnsErrorResultWithoutThrowing()
    {
        var runner = CreateRunner();
        var parameters = new ForkedAgentParams
        {
            ForkLabel = "test-fork",
            MaxTurns = 5,
            CacheSafeParams = new CacheSafeParams
            {
                SystemPrompt = "You are a test agent",
                ModelId = "test-model",
            },
        };

        var ct = TestContext.Current.CancellationToken;
        var result = await runner.RunForkedAgentAsync(parameters, ct);

        result.Should().NotBeNull();
        result.Error.Should().NotBeNull("null chat client should produce structured error, not throw");
        result.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RunForkedAgentAsync_AfterFailure_DoesNotLeaveActiveRun()
    {
        var runner = CreateRunner();
        var parameters = new ForkedAgentParams { ForkLabel = "cleanup-test" };

        var ct = TestContext.Current.CancellationToken;
        try { await runner.RunForkedAgentAsync(parameters, ct); } catch { /* expected */ }

        runner.ActiveRunCount.Should().Be(0,
            "active run must be cleaned up in the finally block even on failure");
    }

    [Fact]
    public async Task RunForkedAgentAsync_WithProgressWriter_ReportsStartedAndFailedLifecycle()
    {
        // 进度旁路：父 run 注入的写入器必须收到 Started；null chat client 走异常
        // 路径收尾必须收到 Failed——Started/Failed 配对是状态栏 agents 徽标计数的前提。
        var channel = System.Threading.Channels.Channel.CreateUnbounded<object>();
        var previous = OneCodeAgentRunContext.SubagentEventWriter;
        OneCodeAgentRunContext.SubagentEventWriter = channel.Writer;
        try
        {
            var runner = CreateRunner();
            var parameters = new ForkedAgentParams
            {
                ForkLabel = "explore-1",
                MaxTurns = 5,
                CacheSafeParams = new CacheSafeParams
                {
                    SystemPrompt = "You are a test agent",
                    ModelId = "test-model",
                },
            };

            await runner.RunForkedAgentAsync(parameters, TestContext.Current.CancellationToken);

            var updates = new List<SubagentProgressUpdate>();
            while (channel.Reader.TryRead(out var evt))
            {
                if (evt is SubagentProgressUpdate update)
                    updates.Add(update);
            }

            updates.Should().Contain(u => u.Phase == SubagentProgressPhase.Started && u.AgentName == "explore-1");
            updates.Should().Contain(u => u.Phase == SubagentProgressPhase.Failed,
                "null chat client fails the fork — Failed must pair with Started for badge counting");
        }
        finally
        {
            OneCodeAgentRunContext.SubagentEventWriter = previous;
        }
    }

    [Fact]
    public async Task RunForkedAgentAsync_WithoutProgressWriter_CompletesSilently()
    {
        // 反面场景：非流式上下文（GOAL DAG / 测试宿主）无写入器——fork 正常完成且
        // 不产生任何进度副作用（progressWriter 为 null 时全部上报调用为 no-op）。
        var previous = OneCodeAgentRunContext.SubagentEventWriter;
        OneCodeAgentRunContext.SubagentEventWriter = null;
        try
        {
            var runner = CreateRunner();
            var parameters = new ForkedAgentParams { ForkLabel = "silent-fork", MaxTurns = 5 };

            var result = await runner.RunForkedAgentAsync(parameters, TestContext.Current.CancellationToken);

            result.Should().NotBeNull();
            result.Error.Should().NotBeNull("null chat client still produces the structured error");
        }
        finally
        {
            OneCodeAgentRunContext.SubagentEventWriter = previous;
        }
    }

    [Theory]
    [InlineData("Explore", PipelineProfile.Explore)]
    [InlineData("Plan", PipelineProfile.Plan)]
    [InlineData("general-purpose", PipelineProfile.Worker)]
    public void FromAgentType_MapsAgentTypesToProfiles(string agentType, PipelineProfile expected)
    {
        PipelineProfileBehavior.FromAgentType(agentType).Should().Be(expected);
    }

    [Fact]
    public void GetRoleInstruction_ExploreAndPlan_AreNonEmptyOverlays()
    {
        var explore = PipelineProfileBehavior.GetRoleInstruction(PipelineProfile.Explore);
        var plan = PipelineProfileBehavior.GetRoleInstruction(PipelineProfile.Plan);

        explore.Should().Contain("Explore sub-agent");
        plan.Should().Contain("Plan sub-agent");
    }

    [Fact]
    public async Task ComposeExploreSystem_KeepsHarnessAndRoleOverlaySeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness."));
        var composer = new PromptComposer(promptManager);

        var role = PipelineProfileBehavior.GetRoleInstruction(PipelineProfile.Explore)!;
        var harness = await composer.GetHarnessAsync(ct);
        var body = composer.RenderRoleBody(role);

        // MAF composes these two; the product must not pre-join them.
        harness.Should().Contain("Prompt injection defense");
        harness.Should().NotContain("Explore sub-agent");
        body.Should().Contain("Explore sub-agent");
        body.Should().Contain("read-only");
        body.Should().NotContain("Prompt injection defense");
    }

    [Fact]
    public void ExploreProfile_AppliesReadOnlyToolWhitelist()
    {
        var ctx = new PipelineSecurityContext(
            WorkingDirectory: "/work",
            PermissionMode: PermissionMode.Default,
            RulesBySource: null,
            AdditionalWorkingDirectories: null,
            Hook: null,
            VerificationProvider: null,
            EnableVerification: false,
            OrchestrationEventSink: null,
            FileChangeCallback: null,
            ModelId: null,
            ProviderId: null);

        var options = AgentPipelineOptionsFactory.Create(
            PipelineProfile.Explore,
            ctx,
            new PipelineRoleOverrides(MaxToolCalls: 10, ToolLimitMessage: "limit"));

        options.EnableEditPreChecks.Should().BeFalse();
        options.EnableVerification.Should().BeFalse();
        options.IsToolAllowed.Should().NotBeNull();
        options.IsToolAllowed!("Write").Should().BeFalse();
        options.IsToolAllowed!("Read").Should().BeTrue();
        options.IsToolAllowed!("Grep").Should().BeTrue();
    }

    [Theory]
    [InlineData(PipelineProfile.Explore, 12, 8, 12)]   // frontmatter 压过 settings
    [InlineData(PipelineProfile.Worker, 12, 8, 12)]
    [InlineData(PipelineProfile.Explore, null, 8, 8)]   // settings 压过 profile 默认
    [InlineData(PipelineProfile.Worker, null, 8, 8)]
    [InlineData(PipelineProfile.Explore, null, 0, 15)]  // 全未配置 → 只读 profile 默认 15
    [InlineData(PipelineProfile.Plan, null, 0, 15)]
    [InlineData(PipelineProfile.Worker, null, 0, 50)]   // 通用 profile 默认 50
    [InlineData(PipelineProfile.Worker, 0, 0, 1)]       // 非法 ceiling（≤0）夹紧到最小 1
    public void ResolveMaxTurns_CeilingChain_FrontmatterOverSettingsOverProfileDefault(
        PipelineProfile profile,
        int? frontmatterCeiling,
        int configuredMaxTurns,
        int expected)
    {
        ForkedAgentRunner.ResolveMaxTurns(profile, frontmatterCeiling, configuredMaxTurns)
            .Should().Be(expected);
    }

    [Fact]
    public void ResolveEffectiveMaxTurns_RequestTurns_CanOnlyNarrowNotRaise()
    {
        // 请求参数（Agent 工具调用的 maxTurns）只能收窄上限链给出的天花板，
        // 不能抬高——否则模型可以自行派一个无上限的子代理绕过预算。
        var request = new AgentRunRequest(Agent: "general-purpose", Prompt: "p") { MaxTurns = 100 };

        var effective = ForkedAgentRunner.ResolveEffectiveMaxTurns(
            PipelineProfile.Worker, request, configuredMaxTurns: 0);

        effective.Should().Be(50, "请求 100 不得抬高通用 profile 的 50 轮默认上限");
    }

    [Fact]
    public void ResolveEffectiveMaxTurns_RequestTurns_NarrowsTheCeiling()
    {
        var request = new AgentRunRequest(Agent: "Explore", Prompt: "p") { MaxTurns = 3 };

        var effective = ForkedAgentRunner.ResolveEffectiveMaxTurns(
            PipelineProfile.Explore, request, configuredMaxTurns: 0);

        effective.Should().Be(3, "调用方收窄到 3 轮必须生效");
    }

    [Fact]
    public void ResolveEffectiveMaxTurns_FrontmatterCeiling_BoundsRequestedTurns()
    {
        // frontmatter 上限 5 时，即使请求 30 也只允许 5 轮。
        var request = new AgentRunRequest(Agent: "Explore", Prompt: "p")
        {
            MaxTurns = 30,
            MaxTurnsCeiling = 5,
        };

        var effective = ForkedAgentRunner.ResolveEffectiveMaxTurns(
            PipelineProfile.Worker, request, configuredMaxTurns: 0);

        effective.Should().Be(5);
    }

    [Fact]
    public void GetRoleInstruction_Explore_ContainsStopConditions()
    {
        // 无停止条件的 Explore 会把简单调研跑满 30 轮（事故：52 次工具调用）。
        // 角色指令必须带上并行检索与"连续无新信息即收尾"的停止条件。
        var explore = PipelineProfileBehavior.GetRoleInstruction(PipelineProfile.Explore)!;

        explore.Should().Contain("parallel", "必须指示并行检索以压缩轮数");
        explore.Should().Contain("stop", "必须有显式停止条件");
    }

    [Fact]
    public void RunAsync_SettingsMaxTurns_BoundsSubAgentTurns()
    {
        // settings agents.defaults.maxTurns 作为上限链一环：经真实 IConfigManager 读取，
        // 配置值必须传导到生效轮数上限（Explore 事故：52 次工具调用源于上限不可配）。
        var configManager = TestConfigManager.Create(new AppSettings(new Dictionary<string, object?>
        {
            [CoreConstants.ConfigKeys.AgentsDefaultMaxTurns] = 7,
        }));
        var runner = CreateRunner(configManager);

        var (maxTurns, _) = runner.ResolveLimits(
            PipelineProfile.Worker, new AgentRunRequest(Agent: "general-purpose", Prompt: "p"));

        maxTurns.Should().Be(7, "settings 配置的 agents.defaults.maxTurns 必须成为子代理轮数上限");
    }

    [Fact]
    public void RunAsync_SettingsTimeoutZero_DisablesWallClockTimeout()
    {
        // timeoutMinutes ≤0 = 显式关闭墙钟超时（长调研任务场景）。
        var configManager = TestConfigManager.Create(new AppSettings(new Dictionary<string, object?>
        {
            [CoreConstants.ConfigKeys.AgentsDefaultTimeoutMinutes] = 0,
        }));
        var runner = CreateRunner(configManager);

        var (_, timeout) = runner.ResolveLimits(
            PipelineProfile.Worker, new AgentRunRequest(Agent: "general-purpose", Prompt: "p"));

        timeout.Should().BeNull("timeoutMinutes=0 必须关闭墙钟超时");
    }

    [Fact]
    public void RunAsync_TimeoutDefault_TenMinutes()
    {
        var runner = CreateRunner();

        var (_, timeout) = runner.ResolveLimits(
            PipelineProfile.Worker, new AgentRunRequest(Agent: "general-purpose", Prompt: "p"));

        timeout.Should().Be(TimeSpan.FromMinutes(10), "默认墙钟超时 10 分钟（对齐 Gemini CLI 默认）");
    }
}
