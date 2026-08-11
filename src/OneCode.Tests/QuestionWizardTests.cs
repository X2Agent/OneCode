using OneCode.App.Tui;
using OneCode.Core.Tools;
using Terminal.Gui.Input;

namespace OneCode.Tests;

public sealed class QuestionWizardTests
{
    [Fact]
    public void ChoiceQuestion_LeftRight_NavigatesBetweenQuestions()
    {
        var wizard = CreateWizard(QuestionType.SingleChoice, QuestionType.SingleChoice);

        wizard.HandleKey(Key.CursorRight).Should().BeTrue();
        wizard.CurrentIndex.Should().Be(1);
        wizard.HandleKey(Key.CursorLeft).Should().BeTrue();
        wizard.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void RenderAsLines_UsesSharedInformationRequestCardHeader()
    {
        var wizard = CreateWizard(QuestionType.ShortText, QuestionType.ShortText);

        var text = string.Join("\n", wizard.RenderAsLines().Select(line => line.FullText));

        text.Should().Contain("需要补充信息");
        text.Should().Contain("测试向导");
        text.Should().Contain("1/2");
        text.Should().Contain("[简答]");
    }

    [Theory]
    [InlineData(QuestionType.ShortText)]
    [InlineData(QuestionType.LongText)]
    public void TextQuestion_BareLeftRight_RemainsAvailableForCaretMovement(QuestionType type)
    {
        var wizard = CreateWizard(type, type);

        wizard.HandleKey(Key.CursorRight).Should().BeFalse();
        wizard.CurrentIndex.Should().Be(0);
    }

    [Theory]
    [InlineData(QuestionType.ShortText)]
    [InlineData(QuestionType.LongText)]
    public void TextQuestion_NavigationHints_UseAltModifiedArrows(QuestionType type)
    {
        var wizard = CreateWizard(type, type);

        var firstPage = string.Join("\n", wizard.RenderAsLines().Select(line => line.FullText));
        firstPage.Should().Contain("Alt+→ 下一题");
        firstPage.Should().NotContain("Alt+← 上一题");

        wizard.MoveToNext();
        var secondPage = string.Join("\n", wizard.RenderAsLines().Select(line => line.FullText));
        secondPage.Should().Contain("Alt+← 上一题");
    }

    private static QuestionWizard CreateWizard(QuestionType first, QuestionType second)
        => new(
            "测试向导",
            [CreateQuestion("first", first), CreateQuestion("second", second)]);

    private static WizardQuestion CreateQuestion(string id, QuestionType type)
        => type switch
        {
            QuestionType.SingleChoice => new WizardQuestion(
                id,
                "请选择",
                type,
                options: ["A", "B"]),
            _ => new WizardQuestion(id, "请输入", type),
        };
}
