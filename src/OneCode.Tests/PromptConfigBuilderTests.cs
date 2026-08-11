using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Context;
using OneCode.App.Services.Skills;
using OneCode.Core.Memory;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Mcp;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// Verifies PromptConfigBuilder composes harness + default without re-appending
/// memory / user context (regression for double-append).
/// </summary>
public sealed class PromptConfigBuilderTests
{
    [Fact]
    public async Task BuildDefaultPromptContentAsync_DoesNotDuplicateMemoryOrUserContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new PromptManager();
        manager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness."));
        manager.RegisterTemplate(new PromptTemplate(
            PromptComposer.DefaultPromptName,
            """
            You are OneCode.
            # Project Context
            {{user_context}}
            # Memory
            {{memory_section}}
            # Environment
            {{system_context}}
            # Available tools
            {{available_tools}}
            """));

        var composer = new PromptComposer(manager);
        var builder = new PromptConfigBuilder(
            NullLogger<PromptConfigBuilder>.Instance,
            configManager: null!,
            memoryService: null!,
            contextBuilder: null!,
            runtimeDeps: null!,
            promptComposer: composer,
            toolMetadataRegistry: new ToolMetadataRegistry());

        const string user = "USER_SENTINEL_UNIQUE";
        const string mem = "MEM_SENTINEL_UNIQUE";

        var result = await builder.BuildDefaultPromptContentAsync(
            "sys", user, mem, availableTools: "", ct);

        result.Should().Contain("Shared harness.");
        result.Should().Contain("Prompt injection defense");
        result.Should().Contain(user);
        result.Should().Contain(mem);
        result.Split(user, StringSplitOptions.None).Length.Should().Be(2);
        result.Split(mem, StringSplitOptions.None).Length.Should().Be(2);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_ComposesAllContextSections_AndConnectsMcp()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new PromptManager();
        manager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness."));
        manager.RegisterTemplate(new PromptTemplate(
            PromptComposer.DefaultPromptName,
            """
            You are OneCode.
            # Project Context
            {{user_context}}
            # Memory
            {{memory_section}}
            # Environment
            {{system_context}}
            # Available tools
            {{available_tools}}
            """));

        var composer = new PromptComposer(manager);

        var configManager = TestConfigManager.Create();
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.LoadMemoryPromptAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("MEMORY_SENTINEL");

        var processRunner = Substitute.For<IProcessRunner>();
        var gitInfo = new GitInfo(processRunner, NullLogger<GitInfo>.Instance);
        var contextBuilder = new ContextBuilder(gitInfo, processRunner, NullLogger<ContextBuilder>.Instance);

        var mcpManager = Substitute.For<IMcpConnectionManager>();
        var mcpSkillsIntegrator = new McpSkillsIntegrator(mcpManager, NullLogger<McpSkillsIntegrator>.Instance);
        var skillProviderHolder = new SkillProviderHolder(new AgentSkillsProviderBuilder().Build());
        var runtimeDeps = new PromptRuntimeDependencies(
            mcpManager, mcpSkillsIntegrator, skillProviderHolder, new SkillCatalog(Path.GetTempPath()));

        var builder = new PromptConfigBuilder(
            NullLogger<PromptConfigBuilder>.Instance,
            configManager,
            memoryService,
            contextBuilder,
            runtimeDeps,
            composer,
            new ToolMetadataRegistry());

        var result = await builder.BuildSystemPromptAsync(memoryQuery: null, ct);

        // Harness is prepended
        result.Should().Contain("Shared harness.");
        result.Should().Contain("Prompt injection defense");
        // Memory section is injected
        result.Should().Contain("MEMORY_SENTINEL");
        result.Split("MEMORY_SENTINEL", StringSplitOptions.None).Length.Should().Be(2,
            "memory section must appear exactly once (no duplication)");
        // MCP ConnectAllAsync was called
        await mcpManager.Received(1).ConnectAllAsync(Arg.Any<CancellationToken>());
    }
}
