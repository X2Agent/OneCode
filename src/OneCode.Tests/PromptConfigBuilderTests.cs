using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Context;
using OneCode.Core.IO;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// Verifies PromptConfigBuilder composes harness + default without duplicating user context
/// or embedding memory (memory is injected dynamically via MemoryContextProvider).
/// </summary>
public sealed class PromptConfigBuilderTests
{
    [Fact]
    public async Task BuildDefaultPromptContentAsync_RendersUserAndSystemContextWithoutHarness()
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
            # Environment
            {{system_context}}
            # Available tools
            {{available_tools}}
            """));

        var composer = new PromptComposer(manager);
        var builder = new PromptConfigBuilder(
            configManager: null!,
            contextBuilder: null!,
            promptComposer: composer,
            toolMetadataRegistry: new ToolMetadataRegistry());

        const string user = "USER_SENTINEL_UNIQUE";

        var result = await builder.BuildDefaultPromptContentAsync(
            "sys", user, availableTools: "", ct);

        result.Should().NotContain("Shared harness.");
        result.Should().NotContain("Prompt injection defense");
        result.Should().Contain(user);
        result.Split(user, StringSplitOptions.None).Length.Should().Be(2);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_ComposesContextSections()
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
            # Environment
            {{system_context}}
            # Available tools
            {{available_tools}}
            """));

        var composer = new PromptComposer(manager);
        var configManager = TestConfigManager.Create();

        var processRunner = Substitute.For<IProcessRunner>();
        var gitInfo = new GitInfo(processRunner, NullLogger<GitInfo>.Instance);
        var contextBuilder = new ContextBuilder(gitInfo, processRunner, NullLogger<ContextBuilder>.Instance);

        var builder = new PromptConfigBuilder(
            configManager,
            contextBuilder,
            composer,
            new ToolMetadataRegistry());

        var result = await builder.BuildSystemPromptAsync(ct);

        result.Should().NotContain("Shared harness.");
        result.Should().Contain("You are OneCode.");
    }
}
