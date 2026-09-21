using OneCode.App.Services;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

/// <summary>
/// §4.6 行为契约：产品只负责加载与渲染，合成交给 MAF。
/// </summary>
/// <remarks>
/// 修复前 <c>PromptComposer.Compose</c> 手工拼接 harness + body，然后整体作为 agent 指令下发。
/// MAF 已有确定的合成顺序（<c>HarnessInstructions</c> 在前，<c>ChatOptions.Instructions</c> 在后，
/// 中间两个换行），手工拼接属于重复职责，且调用方预拼接会让通用片段重复进入主体。
/// </remarks>
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

    /// <summary>
    /// 主体渲染必须替换全部占位符，且不包含 harness 片段——否则 MAF 合成后通用片段会出现两次。
    /// </summary>
    [Fact]
    public async Task RenderMainBodyAsync_RendersPlaceholdersWithoutHarness()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = CreateComposer(
            harness: "HARNESS_SENTINEL",
            defaultBody: """
                Identity line.
                # Project Context
                {{user_context}}
                # Memory
                {{memory_section}}
                # Environment
                {{system_context}}
                """);

        var body = await composer.RenderMainBodyAsync(
            systemContext: "SYS_CTX",
            userContext: "USER_CTX_ONCE",
            memorySection: "MEM_ONCE",
            availableTools: null,
            ct);

        body.Should().Contain("Identity line.");
        body.Should().Contain("USER_CTX_ONCE");
        body.Should().Contain("MEM_ONCE");
        body.Should().Contain("SYS_CTX");
        body.Should().NotContain("HARNESS_SENTINEL",
            "the harness fragment is a separate input; including it here would duplicate it after MAF composes");
        body.Split("USER_CTX_ONCE", StringSplitOptions.None).Length.Should().Be(2,
            "user context must appear exactly once");
        body.Split("MEM_ONCE", StringSplitOptions.None).Length.Should().Be(2,
            "memory section must appear exactly once");
    }

    /// <summary>角色正文同样不含 harness，且 memory 提示必须在正文之后。</summary>
    [Fact]
    public void RenderRoleBody_AppendsMemoryHintWithoutHarness()
    {
        var composer = CreateComposer(harness: "HARNESS_BLOCK");

        var body = composer.RenderRoleBody("ROLE_BODY_SENTINEL");

        body.Should().NotContain("HARNESS_BLOCK");
        body.Should().Contain("search_memories");
        body.IndexOf("ROLE_BODY_SENTINEL", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("search_memories", StringComparison.Ordinal),
                "hint must come after the role body, not before it");
    }

    /// <summary>
    /// 反证：通用片段必须可单独取得（MAF 的 <c>HarnessInstructions</c> 输入），
    /// 缺失时 fail-fast，不能静默回退到 MAF 默认文案掩盖产品文件缺失。
    /// </summary>
    [Fact]
    public async Task GetHarnessAsync_Missing_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = new PromptComposer(new PromptManager());

        var act = async () => await composer.GetHarnessAsync(ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system/harness*");
    }

    /// <summary>主模板缺失同样必须失败，而不是让空主体悄悄进入请求。</summary>
    [Fact]
    public async Task RenderMainBodyAsync_MissingTemplate_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var composer = CreateComposer();

        var act = () => composer.RenderMainBodyAsync("sys", "user", null, null, ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system/default*");
    }
}
