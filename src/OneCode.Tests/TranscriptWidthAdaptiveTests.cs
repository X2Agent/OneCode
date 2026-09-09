using OneCode.App.Services.Lsp;
using OneCode.App.Tui;
using OneCode.Core.Domain;

using OneCode.Core.Tools;

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

    [Fact]
    public void PlanCard_LongTitleAndLabelWithoutAssignee_TruncatesToViewport()
    {
        // 旧实现标题与无归属步骤标签直出，超宽部分被绘制层硬裁掉。
        var steps = new List<PlanStep>
        {
            new(string.Concat(Enumerable.Repeat("超长步骤标签", 20))),
        };
        var lines = ChatBlockRenderers.RenderPlanCard(
            string.Concat(Enumerable.Repeat("超长计划标题", 20)), steps, viewWidth: 50);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 50);
    }

    [Fact]
    public void DiffBlock_WrappedContinuationLines_StaysWithinViewWidth()
    {
        // 旧实现按首行预算（viewWidth - 4）折行，续行前缀 6 列导致超宽 2 列
        // 被绘制层裁掉行尾字符。
        var content = new string('x', 200);
        var lines = ChatBlockRenderers.RenderDiffBlock(
            "a.cs", [content], [], viewWidth: 50);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 50);
        // 换行不得丢内容：所有行拼接后 x 数量不变。
        string.Concat(lines.Select(l => l.FullText)).Count(ch => ch == 'x').Should().Be(200);
    }

    [Fact]
    public void Markdown_LongHeading_WrapsWithinViewWidth()
    {
        // 旧实现标题不换行直出，超宽部分被绘制层裁掉。
        var heading = string.Concat(Enumerable.Repeat("标题文字", 20));
        var lines = MarkdownRenderer.Render($"# {heading}", viewWidth: 40);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.Text))
            .Should().OnlyContain(w => w <= 40);
        string.Concat(lines.Select(l => l.Text.Trim())).Should().Be(heading);
    }

    [Fact]
    public void Markdown_FencedCodeOverWideLine_WrapsWithinBoxWidth()
    {
        // 旧实现超宽代码行不换行，右侧边框被推出视口、代码尾部不可见。
        var code = new string('x', 100);
        var lines = MarkdownRenderer.Render($"```csharp\n{code}\n```", viewWidth: 40);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.Text))
            .Should().OnlyContain(w => w <= 40);
        // 代码内容逐字保留（硬换行不丢字符），且每行代码都有右边框。
        string.Concat(lines.Select(l => l.Text)).Count(ch => ch == 'x').Should().Be(100);
        lines.Where(l => l.Text.Contains('x'))
            .Should().OnlyContain(l => l.Text.TrimEnd().EndsWith("│"));
    }

    [Fact]
    public void Markdown_TableScaledTinyColumns_RowsStayWithinViewWidth()
    {
        // 缩放后 Math.Max(4, …) 下限会把总宽顶回上限之上（多窄列场景）。
        var md = "| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | " + new string('值', 60) + " |";
        var lines = MarkdownRenderer.Render(md, viewWidth: 60);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.Text))
            .Should().OnlyContain(w => w <= 60);
    }

    [Fact]
    public void ToolResultMessage_ContentExactlyAtBudget_KeepsEveryCharacter()
    {
        // 旧实现 TruncateLine 无守卫调用 TruncateByWidth，恰好满宽的行尾
        // 字符被误替换为省略号。
        var renderer = new MessageFlowRenderer { CurrentWidth = 80 };
        var exact = new string('x', 76); // 预算 = 80 - 2 - 2

        var text = JoinRendered(renderer, exact);

        text.Should().NotContain("…");
        text.Count(ch => ch == 'x').Should().Be(76);
    }

    [Fact]
    public void ToolResultMessage_OverWideContent_TruncatesWithEllipsis()
    {
        var renderer = new MessageFlowRenderer { CurrentWidth = 80 };

        var text = JoinRendered(renderer, new string('x', 200));

        text.Should().Contain("…");
    }

    [Fact]
    public void ErrorBlock_CjkSummary_TruncatesByDisplayWidth()
    {
        // 旧实现按字符数截断摘要，CJK 摘要显示宽度成倍超宽。
        var firstLine = string.Concat(Enumerable.Repeat("错误信息很长", 30));
        var lines = ChatTranscriptView.RenderErrorBlock(
            firstLine + "\n第二行详情", contentWidth: 80);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 80);
        var text = string.Join("\n", lines.Select(l => l.FullText));
        text.Should().Contain("…");
        text.Should().Contain("第二行详情");
    }

    [Fact]
    public void ErrorBlock_ShortFirstLine_NotRepeatedInDetails()
    {
        var lines = ChatTranscriptView.RenderErrorBlock("短摘要\n详情内容", contentWidth: 80);
        var text = string.Join("\n", lines.Select(l => l.FullText));

        // 首行已完整出现在摘要行中，详情不应重复展示（Occurrences == 1）。
        text.Split("短摘要").Should().HaveCount(2);
        text.Should().Contain("详情内容");
    }

    [Fact]
    public void ErrorBlock_SingleOverLongLine_ShowsFullTextWithoutClick()
    {
        // 旧实现 errorLines.Length > 1 保护使单行超长错误（如 Goal 脏工作树异常）
        // 永远没有详情行，摘要截断后内容不可达。
        var message = string.Concat(Enumerable.Repeat("Goal isolated execution requires a clean tree", 5));
        var lines = ChatTranscriptView.RenderErrorBlock(message, contentWidth: 80);
        var text = string.Join("", lines.Select(l => l.FullText));

        lines.Should().Contain(l => l.FullText.Contains("…")); // 摘要行仍截断
        lines.Should().HaveCountGreaterThan(1);                // 详情行存在
        text.Should().Contain("clean tree");                   // 原文尾部完整可见
        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 80);
    }

    [Fact]
    public void ErrorBlock_SingleOverLongCjkLine_WrapsWithoutContentLoss()
    {
        // CJK 单行超长错误按显示宽度换行，内容逐字保留。
        var message = string.Concat(Enumerable.Repeat("目标执行需要干净的Git工作树", 10));
        var lines = ChatTranscriptView.RenderErrorBlock(message, contentWidth: 80);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 80);
        lines.Should().HaveCountGreaterThan(1);
        // 摘要行之外的详情行拼接后覆盖完整原文。
        var detail = string.Join("", lines.Skip(1).Select(l => l.FullText.Trim()));
        detail.Should().Be(message);
    }

    [Fact]
    public void InlineSelector_LongOptionLabel_TruncatesToViewport()
    {
        var options = new[]
        {
            new InlineSelectorOption("a", new string('x', 50), "描述"),
            new InlineSelectorOption("b", "短标签", new string('y', 100)),
        };
        var lines = InlineSelector.RenderAsLines("标题", options, selectedIndex: 0, viewWidth: 40);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 40);
        string.Join("\n", lines.Select(l => l.FullText)).Should().Contain("…");
    }

    [Fact]
    public void QuestionWizard_LongOption_TruncatesToViewport()
    {
        var wizard = new QuestionWizard(
            "向导",
            [new WizardQuestion("q1", "请选择", QuestionType.SingleChoice,
                options: [new string('x', 200), "B"])]);

        var lines = wizard.RenderAsLines(viewWidth: 40);

        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 40);
    }

    private static string JoinRendered(MessageFlowRenderer renderer, string content)
        => string.Join("\n", renderer.RenderMessage(new ToolResultMessage(
            "id", "t1", "Read", content, IsError: false, DateTimeOffset.UtcNow))
            .Select(l => l.FullText));
}
