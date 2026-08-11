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
            new CostTracker());

        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness for forks."));

        return new ForkedAgentRunner(
            logger: NullLogger<ForkedAgentRunner>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            serviceProvider: Substitute.For<IServiceProvider>(),
            sharedContextBuilder: TestSupport.TestAgentContextProviderAssembly.Create(
                modelManager: new ModelManager(configManager, new ModelCatalogStore()),
                modeProvider: modeProvider).Shared,
            pipelineFactory: new SubAgentPipelineFactory(
                modeProvider,
                Substitute.For<IHookExecutionService>(),
                Substitute.For<IVerificationProvider>(),
                Substitute.For<IPermissionChecker>(),
                Substitute.For<IAppStateAccessor>(),
                new CostTracker(),
                configManager),
            runtime: new ForkedAgentRuntimeDependencies(
                null!,
                Substitute.For<IModelManager>(),
                Substitute.For<IWorkingDirectoryAccessor>(),
                new ToolMetadataRegistry(),
                new CompactionProviderBuilder(
                    null!,
                    NullLoggerFactory.Instance,
                    Substitute.For<IModelManager>(),
                    new OneCode.App.Services.Compact.CompactPromptBuilder(new PromptManager()))),
            promptComposer: new PromptComposer(promptManager));
    }

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
    public async Task ComposeExploreSystem_IncludesHarnessAndRoleOverlay()
    {
        var ct = TestContext.Current.CancellationToken;
        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness."));
        var composer = new PromptComposer(promptManager);

        var role = PipelineProfileBehavior.GetRoleInstruction(PipelineProfile.Explore)!;
        var system = await composer.ComposeWithRoleAsync(role, ct);

        system.Should().Contain("Prompt injection defense");
        system.Should().Contain("Explore sub-agent");
        system.Should().Contain("read-only");
    }

    [Fact]
    public void ExploreProfile_AppliesReadOnlyToolWhitelist()
    {
        var ctx = new PipelineSecurityContext(
            WorkingDirectory: "/work",
            PermissionMode: PermissionMode.Default,
            RulesBySource: null,
            AdditionalWorkingDirectories: null,
            SessionAllowlist: null,
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

        options.EnableBehaviorContracts.Should().BeFalse();
        options.EnableVerification.Should().BeFalse();
        options.IsToolAllowed.Should().NotBeNull();
        options.IsToolAllowed!("Write").Should().BeFalse();
        options.IsToolAllowed!("Read").Should().BeTrue();
        options.IsToolAllowed!("Grep").Should().BeTrue();
    }
}
