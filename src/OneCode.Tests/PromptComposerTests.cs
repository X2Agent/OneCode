using OneCode.App.Services;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

/// <summary>
/// 行为契约：产品只负责加载与渲染，合成交给 MAF。
/// </summary>
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
                # Environment
                {{system_context}}
                """);

        var body = await composer.RenderMainBodyAsync(
            systemContext: "SYS_CTX",
            userContext: "USER_CTX_ONCE",
            availableTools: null,
            ct);

        body.Should().Contain("Identity line.");
        body.Should().Contain("USER_CTX_ONCE");
        body.Should().Contain("SYS_CTX");
        body.Should().NotContain("HARNESS_SENTINEL",
            "the harness fragment is a separate input; including it here would duplicate it after MAF composes");
        body.Split("USER_CTX_ONCE", StringSplitOptions.None).Length.Should().Be(2,
            "user context must appear exactly once");
    }

    /// <summary>角色正文同样不含 harness。</summary>
    [Fact]
    public void RenderRoleBody_TrimsEndWithoutHarness()
    {
        var composer = CreateComposer(harness: "HARNESS_BLOCK");

        var body = composer.RenderRoleBody("ROLE_BODY_SENTINEL   \n");

        body.Should().NotContain("HARNESS_BLOCK");
        body.Should().Be("ROLE_BODY_SENTINEL");
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

        var act = () => composer.RenderMainBodyAsync("sys", "user", null, ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system/default*");
    }
}
