using OneCode.App.Services;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

public sealed class PromptComposerTests
{
    private static PromptComposer CreateComposer(
        string harness = "# Prompt injection defense — MANDATORY\nHarness body.",
        string? defaultBody = null)
    {
        var manager = new PromptManager();
        manager.RegisterTemplate(new PromptTemplate(PromptComposer.HarnessPromptName, harness));
        if (defaultBody is not null)
            manager.RegisterTemplate(new PromptTemplate(PromptComposer.DefaultPromptName, defaultBody));
        return new PromptComposer(manager);
    }

    [Fact]
    public async Task ComposeMainAsync_PrependsHarnessAndRendersPlaceholdersOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = CreateComposer(
            harness: "HARNESS_SENTINEL\n# Prompt injection defense",
            defaultBody: """
                Identity line.
                # Project Context
                {{user_context}}
                # Memory
                {{memory_section}}
                # Environment
                {{system_context}}
                """);

        var result = await composer.ComposeMainAsync(
            systemContext: "SYS_CTX",
            userContext: "USER_CTX_ONCE",
            memorySection: "MEM_ONCE",
            availableTools: null,
            ct);

        result.Should().StartWith("HARNESS_SENTINEL");
        result.Should().Contain("Prompt injection defense");
        result.Should().Contain("Identity line.");
        result.Should().Contain("USER_CTX_ONCE");
        result.Should().Contain("MEM_ONCE");
        result.Should().Contain("SYS_CTX");
        result.Split("USER_CTX_ONCE", StringSplitOptions.None).Length.Should().Be(2,
            "user context must appear exactly once");
        result.Split("MEM_ONCE", StringSplitOptions.None).Length.Should().Be(2,
            "memory section must appear exactly once");
    }

    [Fact]
    public async Task ComposeWithRoleAsync_PrependsHarnessToRoleBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = CreateComposer(harness: "HARNESS_BLOCK");

        var result = await composer.ComposeWithRoleAsync(
            "You are a Plan sub-agent designing implementation approaches.", ct);

        result.Should().Contain("HARNESS_BLOCK");
        result.Should().Contain("Plan sub-agent designing implementation approaches");
        result.IndexOf("HARNESS_BLOCK", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("Plan sub-agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComposeWithRoleAsync_AppendsMemoryRecallHintAfterRoleBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = CreateComposer(harness: "HARNESS_BLOCK");

        var result = await composer.ComposeWithRoleAsync("ROLE_BODY_SENTINEL", ct);

        result.Should().Contain("search_memories");
        result.IndexOf("ROLE_BODY_SENTINEL", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("search_memories", StringComparison.Ordinal),
                "hint must come after the role body, not before it");
    }

    [Fact]
    public async Task GetHarnessAsync_Missing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = new PromptComposer(new PromptManager());

        var act = async () => await composer.GetHarnessAsync(ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system/harness*");
    }
}
