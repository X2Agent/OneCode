using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Coordinator;
using OneCode.App.Session;
using OneCode.App.Tools;
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
/// 接线守卫：产品 <c>system/harness.prompt</c> 片段必须经每条 agent 组装路径抵达模型。
/// </summary>
/// <remarks>
/// <para>
/// 缺陷背景：<c>HarnessInstructions</c> 曾无任何生产赋值点——Team 与 Fork 路径把片段取进局部变量
/// 后丢弃，所有 agent 于是回落到 MAF 通用默认指令，「敏感文件保护 / prompt 注入防御」从未到达模型。
/// 这些用例断言的正是「取到并传下去」这一步，而不是片段内容。
/// </para>
/// <para>
/// §4.6 缺陷背景：<c>ChatOptions.Instructions</c> 曾被文档、ADR 与 <see cref="PromptComposer"/> 描述为
/// 主体的唯一入口，实际却无任何生产赋值点——主体被调用方自行拼成一条 system 消息。现在三条路径
/// （Main / Fork / Team）都把主体交给 <c>ChatOptions.Instructions</c>，本文件同时守「主体不得再以
/// system 消息重复下发」。
/// </para>
/// <para>
/// 断言目标是模型实际收到的请求（<see cref="CapturingChatClient"/>），因为产品侧字段非空并不能证明
/// 它穿过了 MAF 的合成层。分工：本文件守「有没有传」，
/// <see cref="HarnessInstructionsCompositionTests"/> 守「MAF 怎么合成」。
/// </para>
/// </remarks>
public sealed class HarnessInstructionsWiringTests
{
    private const string HarnessFragment = "HARNESS_FRAGMENT_SENTINEL: sensitive-file protection";
    private const string RoleBody = "ROLE_BODY_SENTINEL: explore the codebase";

    private static PromptManager CreatePromptManager()
    {
        var manager = new PromptManager();
        manager.RegisterTemplate(new PromptTemplate(PromptComposer.HarnessPromptName, HarnessFragment));
        // 压缩策略在管道构建期加载 system/compact，缺失即 fail-fast。
        manager.RegisterTemplate(new PromptTemplate("system/compact", "COMPACT_PROMPT_SENTINEL"));
        return manager;
    }

    private static IWorkingDirectoryAccessor CreateWorkingDirectoryAccessor()
    {
        var accessor = Substitute.For<IWorkingDirectoryAccessor>();
        accessor.WorkingDirectory.Returns(Path.GetTempPath());
        return accessor;
    }

    private static SubAgentPipelineFactory CreateSubAgentPipelineFactory(IPermissionModeProvider modeProvider) =>
        new(modeProvider,
            Substitute.For<IHookExecutionService>(),
            Substitute.For<IVerificationProvider>(),
            Substitute.For<IPermissionChecker>(),
            Substitute.For<IAppStateAccessor>(),
            new TokenLedger(),
            TestConfigManager.Create());

    /// <summary>
    /// Main 组装漏斗：<c>AgentPipelineBuilder</c> 必须把片段交给 MAF，且片段在前、主体在后。
    /// </summary>
    [Fact]
    public async Task BuildChatClientAgent_ForwardsHarnessFragmentAheadOfAgentBody()
    {
        var client = new CapturingChatClient();

        var handle = AgentPipelineBuilder.BuildChatClientAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = client,
            Name = "main-agent",
            ChatOptions = new ChatOptions { Instructions = RoleBody },
            LoggerFactory = NullLoggerFactory.Instance,
            ServiceProvider = Substitute.For<IServiceProvider>(),
            HarnessInstructions = HarnessFragment,
            PipelineOptions = new AgentPipelineOptions
            {
                WorkingDirectory = Path.GetTempPath(),
                EnableToolApproval = false,
                EnableEditTransaction = false,
                EnableToolResultBudget = false,
                EnableStateMachine = false,
                EnableSafetyInvariants = false,
                EnableBehaviorContracts = false,
                EnableTaskRecovery = false,
            },
        });

        var agent = handle.Agent;
        await agent.RunAsync(
            "hello",
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var instructions = client.LastInstructions;
        instructions.Should().NotBeNull("the model must receive instructions at all");
        instructions.Should().Contain(HarnessFragment);
        instructions!.IndexOf(HarnessFragment, StringComparison.Ordinal)
            .Should().BeLessThan(instructions.IndexOf(RoleBody, StringComparison.Ordinal),
                "MAF composes the harness fragment ahead of the agent body");
        client.LastMessages.Should().NotContain(m => m.Text.Contains(RoleBody, StringComparison.Ordinal),
            "the agent body must travel as instructions only — a system message would duplicate it");
    }

    /// <summary>
    /// Main 调用方：<c>MainAgentRunOptions.SystemPrompt</c> 必须落到 <c>ChatOptions.Instructions</c>，
    /// 且不得同时拼成 system 消息。
    /// </summary>
    [Fact]
    public void MainAgentRunner_BodyTravelsAsInstructionsNotAsSystemMessage()
    {
        var options = new MainAgentRunOptions
        {
            SystemPrompt = RoleBody,
            UserPrompt = "hello",
        };

        MainAgentRunner.BuildChatOptions(options).Instructions.Should().Be(RoleBody,
            "the agent body is the MAF instructions input, composed with the harness fragment by MAF");
        MainAgentRunner.BuildMessages(options)
            .Should().NotContain(m => m.Role == ChatRole.System,
                "the body must not be sent a second time as a system message");
    }

    /// <summary>
    /// 反证：没有主体时不得凭空造出指令，否则 MAF 会拿到空指令而不是回落默认文案。
    /// </summary>
    [Fact]
    public void MainAgentRunner_EmptySystemPrompt_LeavesInstructionsUnset()
    {
        var options = new MainAgentRunOptions { SystemPrompt = string.Empty, UserPrompt = "hello" };

        MainAgentRunner.BuildChatOptions(options).Instructions.Should().BeNull();
        MainAgentRunner.BuildMessages(options).Should().NotContain(m => m.Role == ChatRole.System);
    }

    /// <summary>
    /// Team 路径：片段经 <c>HarnessInstructions</c>、角色正文经 <c>ChatOptions.Instructions</c> 下发，
    /// 两段由 MAF 合成（片段在前）。
    /// </summary>
    [Fact]
    public async Task TeamAgentFactory_ForwardsHarnessFragmentToModel()
    {
        var client = new CapturingChatClient();
        var promptManager = CreatePromptManager();
        var modeProvider = new PermissionModeProvider(TestConfigManager.Create());
        var (shared, main) = TestAgentContextProviderAssembly.Create(modeProvider: modeProvider);

        var agentFactory = new TeamAgentFactory(
            client,
            NullLoggerFactory.Instance,
            NullLogger<TeamAgentFactory>.Instance,
            Substitute.For<IServiceProvider>(),
            Substitute.For<IModelManager>(),
            promptManager,
            new PromptComposer(promptManager),
            new TeamAgentToolSources(
                Substitute.For<ICacheSafeParamsProvider>(),
                new ToolCatalog(new Lazy<List<AIFunction>>(() => []), new ToolMetadataRegistry(), null)),
            new TeamAgentPipelineDependencies(
                new AgentContextPipeline(shared, main),
                CreateSubAgentPipelineFactory(modeProvider),
                new CompactionStrategyFactory(
                    client,
                    Substitute.For<IModelManager>(),
                    new CompactPromptBuilder(promptManager)),
                Substitute.For<ISessionConversationAccess>(),
                new ToolMetadataRegistry()));

        var agent = await agentFactory.BuildAgentAsync(
            new TeamMember("member-1", "executor", RoleBody),
            workingDirectory: Path.GetTempPath());

        await agent.RunAsync(
            "hello",
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        client.LastInstructions.Should().Contain(HarnessFragment,
            "the Team path must hand the shared fragment to MAF instead of dropping it");
        client.LastInstructions.Should().Contain(RoleBody,
            "the role body is the MAF instructions input for the Team member");
        client.LastInstructions!.IndexOf(HarnessFragment, StringComparison.Ordinal)
            .Should().BeLessThan(client.LastInstructions.IndexOf(RoleBody, StringComparison.Ordinal),
                "MAF composes the harness fragment ahead of the role body");
        client.LastMessages.Should().NotContain(m => m.Text.Contains(RoleBody, StringComparison.Ordinal),
            "the role body must not be sent a second time as a system message");
    }

    /// <summary>
    /// Fork（Worker，无角色 overlay）路径：片段仍必须经 <c>HarnessInstructions</c> 抵达模型。
    /// </summary>
    /// <remarks>
    /// 反证：Worker 分支没有角色 overlay，曾与片段共用「有 overlay 才传」的条件。但父级
    /// <c>CacheSafeParams.SystemPrompt</c> 自 §4.6 起只承载主 Agent 正文（片段已移到
    /// <c>HarnessInstructions</c>），所以 Worker 分支拿不到片段时既丢失产品防护指引，又会静默
    /// 回落到 MAF 通用默认指令。
    /// </remarks>
    [Fact]
    public async Task ForkedAgentRunner_WorkerForkStillCarriesHarnessFragment()
    {
        const string ParentBody = "PARENT_BODY_SENTINEL: cache-safe main body";
        var client = new CapturingChatClient();
        var promptManager = CreatePromptManager();
        var modeProvider = new PermissionModeProvider(TestConfigManager.Create());
        var modelManager = Substitute.For<IModelManager>();
        var workingDirectory = CreateWorkingDirectoryAccessor();
        var (shared, main) = TestAgentContextProviderAssembly.Create(modeProvider: modeProvider);

        var runner = new ForkedAgentRunner(
            NullLogger<ForkedAgentRunner>.Instance,
            NullLoggerFactory.Instance,
            Substitute.For<IServiceProvider>(),
            new AgentContextPipeline(shared, main),
            CreateSubAgentPipelineFactory(modeProvider),
            new ForkedAgentRuntimeDependencies(
                client,
                modelManager,
                workingDirectory,
                new ToolMetadataRegistry(),
                new CompactionStrategyFactory(
                    client,
                    modelManager,
                    new CompactPromptBuilder(promptManager))),
            new PromptComposer(promptManager));

        var result = await runner.RunAsync(
            new AgentRunRequest(
                Prompt: "hello",
                Agent: "general-purpose",
                CacheSafeParams: new CacheSafeParams
                {
                    SystemPrompt = ParentBody,
                    ModelId = "test-model",
                }),
            TestContext.Current.CancellationToken);

        result.Error.Should().BeNull("the fork must run end-to-end for the wiring assertion to mean anything");
        client.LastInstructions.Should().Contain(HarnessFragment,
            "Worker forks have no role overlay but still need the product harness fragment");
        client.LastInstructions.Should().NotContain(HarnessAgent.DefaultInstructions,
            "a null fragment would silently degrade the Worker fork to MAF's generic instructions");
        client.LastMessages.Should().Contain(m => m.Text.Contains(ParentBody, StringComparison.Ordinal),
            "the inherited cache-safe system prompt stays a system message — it is not the fork's body");
    }

    /// <summary>
    /// Fork（Explore/Plan）路径：片段经 <c>HarnessInstructions</c>、角色 overlay 经
    /// <c>ChatOptions.Instructions</c> 下发，两段由 MAF 合成（片段在前）。
    /// </summary>
    [Fact]
    public async Task ForkedAgentRunner_ForwardsHarnessFragmentToModel()
    {
        var client = new CapturingChatClient();
        var promptManager = CreatePromptManager();
        var modeProvider = new PermissionModeProvider(TestConfigManager.Create());
        var modelManager = Substitute.For<IModelManager>();
        var workingDirectory = CreateWorkingDirectoryAccessor();
        var (shared, main) = TestAgentContextProviderAssembly.Create(modeProvider: modeProvider);

        var runner = new ForkedAgentRunner(
            NullLogger<ForkedAgentRunner>.Instance,
            NullLoggerFactory.Instance,
            Substitute.For<IServiceProvider>(),
            new AgentContextPipeline(shared, main),
            CreateSubAgentPipelineFactory(modeProvider),
            new ForkedAgentRuntimeDependencies(
                client,
                modelManager,
                workingDirectory,
                new ToolMetadataRegistry(),
                new CompactionStrategyFactory(
                    client,
                    modelManager,
                    new CompactPromptBuilder(promptManager))),
            new PromptComposer(promptManager));

        var result = await runner.RunAsync(
            new AgentRunRequest(Prompt: "hello", Agent: "Explore"),
            TestContext.Current.CancellationToken);

        result.Error.Should().BeNull("the fork must run end-to-end for the wiring assertion to mean anything");
        client.LastInstructions.Should().Contain(HarnessFragment,
            "Explore/Plan forks carry the product harness fragment");
        client.LastInstructions.Should().Contain("Explore sub-agent",
            "the role overlay is the MAF instructions input for the fork");
        client.LastInstructions!.IndexOf(HarnessFragment, StringComparison.Ordinal)
            .Should().BeLessThan(client.LastInstructions.IndexOf("Explore sub-agent", StringComparison.Ordinal),
                "MAF composes the harness fragment ahead of the role overlay");
        client.LastMessages.Should().NotContain(m => m.Text.Contains("Explore sub-agent", StringComparison.Ordinal),
            "the role overlay must not be sent a second time as a system message");
    }
}