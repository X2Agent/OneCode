using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
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


    private static ForkedAgentRunner CreateRunner()
    {
        var configManager = TestConfigManager.Create();
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
            runtime: new ForkedAgentRuntimeDependencies(
                null!,
                Substitute.For<IModelManager>(),
                Substitute.For<IWorkingDirectoryAccessor>(),
                new ToolMetadataRegistry(),
                new CompactionStrategyFactory(
                    null!,
                    Substitute.For<IModelManager>(),
                    new OneCode.App.Services.Compact.CompactPromptBuilder(new PromptManager())),
                new SessionIdHolder()),
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
}
