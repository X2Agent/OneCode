using OneCode.App.Query;

namespace OneCode.Tests;

public sealed class NextPromptTagStreamParserTests
{
    [Fact]
    public void Process_TrailerSplitAcrossChunks_SeparatesVisibleTextAndSuggestion()
    {
        var sut = new NextPromptTagStreamParser();

        var first = sut.Process("已完成修改。<onecode-next-pr").ToList();
        var second = sut.Process("ompt>要我运行测试吗？</onecode-next-prompt>").ToList();

        first.Should().ContainSingle(segment => segment.Text == "已完成修改。");
        first.Where(segment => segment.Suggestion is not null).Should().BeEmpty();
        second.Should().ContainSingle(segment => segment.Suggestion == "要我运行测试吗？");
        second.Where(segment => segment.Text is not null).Should().BeEmpty();
    }

    [Fact]
    public void Process_CloseTagSplitAcrossChunks_SuggestionEmittedIntact()
    {
        var sut = new NextPromptTagStreamParser();

        // 闭标签断在 "</onecode-nex" / "t-prompt>"：片 1 必须把半个闭标签留在 carry 里，
        // 片 2 必须先并入 carry 再找闭标签——否则碎片会被吞进建议文本且建议永不产出。
        var first = sut.Process("回答完成。<onecode-next-prompt>要运行测试吗？</onecode-nex").ToList();
        var second = sut.Process("t-prompt>").ToList();

        first.Should().ContainSingle(segment => segment.Text == "回答完成。");
        first.Where(segment => segment.Suggestion is not null).Should().BeEmpty();
        second.Should().ContainSingle(segment => segment.Suggestion == "要运行测试吗？");
        second.Where(segment => segment.Text is not null).Should().BeEmpty();
        sut.Flush().Should().BeNull("完整闭合后不得残留任何缓冲");
    }

    [Fact]
    public void Flush_IncompleteTrailer_PreservesRawContentAsVisibleText()
    {
        var sut = new NextPromptTagStreamParser();

        _ = sut.Process("回答<onecode-next-prompt>要继续吗").ToList();
        var remaining = sut.Flush();

        remaining.Should().Be("<onecode-next-prompt>要继续吗");
    }
}
