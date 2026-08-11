using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class ToolResultSummarizerTests
{
    [Fact]
    public void FormatTarget_UnknownTool_DecodesUnicodeEscapesForDisplay()
    {
        const string input = "{\"message\":\"\\u8bf7\\u9009\\u62e9\\u4ea7\\u54c1\"}";

        var target = ToolResultSummarizer.FormatTarget("CustomTool", input);

        target.Should().Contain("请选择产品");
        target.Should().NotContain("\\u8bf7");
    }

    [Fact]
    public void FormatTarget_AskUserQuestion_ExtractsQuestionInsteadOfRawJson()
    {
        const string input = "{\"question\":\"请选择产品形态\",\"options\":[\"Web\",\"桌面端\"]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestion", input);

        target.Should().Be("请选择产品形态");
        target.Should().NotContain("options");
    }

    [Fact]
    public void FormatTarget_AskUserQuestions_ExtractsWizardTitleInsteadOfRawJson()
    {
        const string input = "{\"title\":\"规划前确认\",\"questions\":[{\"id\":\"scope\",\"question\":\"目标模块？\"}]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestions", input);

        target.Should().Be("规划前确认");
        target.Should().NotContain("questions");
    }
}
