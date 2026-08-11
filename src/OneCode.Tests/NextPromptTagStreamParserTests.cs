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
    public void Flush_IncompleteTrailer_PreservesRawContentAsVisibleText()
    {
        var sut = new NextPromptTagStreamParser();

        _ = sut.Process("回答<onecode-next-prompt>要继续吗").ToList();
        var remaining = sut.Flush();

        remaining.Should().Be("<onecode-next-prompt>要继续吗");
    }
}
