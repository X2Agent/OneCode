using OneCode.App.Services.Lsp;
using OneCode.App.Tui;
using OneCode.Core.Lsp;

namespace OneCode.Tests;

/// <summary>
/// 对话流内容宽度自适应守护：所有渲染进对话流的内容块（模式横幅 / LSP 诊断块 /
/// 计划卡步骤行）在给定视口宽度时，任何一行的显示宽度都不得超过视口
/// （CJK 双宽字符按 2 列计），超长内容以省略号截断而非被终端硬裁切。
/// </summary>
public sealed class TranscriptWidthAdaptiveTests
{
    [Fact]
    public void ModeBanner_NarrowWidth_TruncatesToViewport()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Team, maxWidth: 40);
        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 40);
        lines.Should().Contain(l => l.FullText.Contains("…"));
    }

    [Fact]
    public void ModeBanner_DefaultWidth_KeepsFullText()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Build);
        string.Join("\n", lines.Select(l => l.FullText))
            .Should().Contain("直接执行，适合小改动和探索性任务");
    }

    [Fact]
    public void LspDiagnosticsBlock_CjkMessage_TruncatesByDisplayWidth()
    {
        // 旧实现按字符数（80）截断消息，CJK 双宽字符会溢出视口。
        var diagnostics = new[]
        {
            new LspDiagnostic
            {
                ServerName = "roslyn",
                Uri = "file:///C:/src/a.cs",
                Severity = LspDiagnosticSeverity.Error,
                Message = string.Concat(Enumerable.Repeat("标识符过长导致诊断消息超宽", 20)),
                Range = new LspRange(10, 4, 10, 8),
            },
        };
        var lines = ChatBlockRenderers.RenderLspDiagnosticsBlock("a.cs", diagnostics, viewWidth: 60);
        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 60);
    }

    [Fact]
    public void LspDiagnosticsBlock_ZeroWidth_KeepsLegacyCharTruncation()
    {
        var diagnostics = new[]
        {
            new LspDiagnostic
            {
                ServerName = "roslyn",
                Uri = "file:///C:/src/a.cs",
                Severity = LspDiagnosticSeverity.Warning,
                Message = new string('x', 200),
                Range = new LspRange(0, 0, 0, 1),
            },
        };
        var lines = ChatBlockRenderers.RenderLspDiagnosticsBlock("a.cs", diagnostics);
        lines.Should().Contain(l => l.FullText.Contains("…"));
    }

    [Fact]
    public void PlanCard_CjkStepLabelWithAssignee_RowFitsViewport()
    {
        // 旧实现 padLen 按 .Length 计算，CJK 标签行会错位并溢出视口。
        var steps = new List<PlanStep>
        {
            new(string.Concat(Enumerable.Repeat("重构渲染链路模块", 20)), Assignee: "executor"),
        };
        var lines = ChatBlockRenderers.RenderPlanCard("中文计划", steps, viewWidth: 60);
        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 60);
        lines.Should().Contain(l => l.FullText.Contains("→ executor"));
    }
}
