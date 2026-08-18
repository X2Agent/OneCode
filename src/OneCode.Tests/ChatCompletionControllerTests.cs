using OneCode.App.Tui;
using OneCode.Core.Commands;

namespace OneCode.Tests;

/// <summary>
/// 补全列表显示测试（P2-10）：SlashCommandEntry 的 ArgumentHint 应显示在弹窗中，
/// 并参与关键字过滤。
/// </summary>
public sealed class ChatCompletionControllerTests
{
    [Fact]
    public void UpdateCompletionList_ShowsArgumentHint()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("add-dir", "Add a directory to the project context", CommandSource.Builtin, "<path> [--persist]"),
                new SlashCommandEntry("help", "Show help", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/add");

        controller.CurrentDisplayItems.Should().NotBeNull();
        controller.CurrentDisplayItems!.Should().Contain(item => item.Contains("<path> [--persist]"));
    }

    [Fact]
    public void UpdateCompletionList_MatchesArgumentHintKeyword()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("add-dir", "Add a directory", CommandSource.Builtin, "[--persist]"),
            },
            typeaheadEngine: null);

        // 输入 "persist" 通过 ArgumentHint 命中 add-dir
        controller.UpdateCompletionList("/persist");

        controller.FilteredCommands.Should().Contain(c => c.Name == "add-dir");
    }
}