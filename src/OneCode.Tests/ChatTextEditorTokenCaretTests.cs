using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// ChatTextEditor 的 PUA Token 光标吸附守护：
/// 光标（含鼠标点入、上下移行、Ctrl+词跳）进入 \uE001…\uE002 内部时必须
/// 被吸附到 Token 边界之外，杜绝嵌入编辑撕裂 Token 导致提交时静默丢字。
/// </summary>
public sealed class ChatTextEditorTokenCaretTests
{
    // "ab" + \uE001 + "[Pasted text #1 +3 lines]" + \uE002 + "cd"（总长 31）
    private const string Text = "ab\uE001[Pasted text #1 +3 lines]\uE002cd";
    private const int TokenStart = 2;                        // \uE001
    private const int TokenEnd = 28;                         // \uE002

    [Theory]
    [InlineData(3, true)]   // 首个内容字符：向前吸附
    [InlineData(10, true)]  // Token 正中：向前吸附
    [InlineData(28, true)]  // \uE002 本身：向前吸附
    public void SnapCaret_Forward_MovesPastClosingControlChar(int caret, bool forward)
    {
        ChatTextEditor.SnapCaretOutOfToken(Text, caret, forward).Should().Be(TokenEnd + 1);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(28, false)]
    public void SnapCaret_Backward_MovesToOpeningControlChar(int caret, bool forward)
    {
        ChatTextEditor.SnapCaretOutOfToken(Text, caret, forward).Should().Be(TokenStart);
    }

    [Theory]
    [InlineData(2)]   // \uE001 处：安全边界
    [InlineData(29)]  // \uE002 后一位：安全边界
    [InlineData(0)]   // 文本起点
    [InlineData(31)]  // 文本终点（== Length）
    public void SnapCaret_AtSafeBoundary_ReturnsUnchanged(int caret)
    {
        ChatTextEditor.SnapCaretOutOfToken(Text, caret, forward: true).Should().Be(caret);
        ChatTextEditor.SnapCaretOutOfToken(Text, caret, forward: false).Should().Be(caret);
    }

    [Fact]
    public void SnapCaret_WithoutToken_ReturnsUnchanged()
    {
        ChatTextEditor.SnapCaretOutOfToken("no tokens here", 5, forward: true).Should().Be(5);
        ChatTextEditor.SnapCaretOutOfToken(string.Empty, 0, forward: true).Should().Be(0);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(28, true)]
    [InlineData(2, false)]
    [InlineData(29, false)]
    public void IsInsideToken_MatchesTokenInteriorOnly(int caret, bool expected)
    {
        ChatTextEditor.IsInsideToken(Text, caret).Should().Be(expected);
    }

    [Fact]
    public void IsInsideToken_DetectsCaretPlacedBetweenTokens()
    {
        var text = "\uE001#a\uE002 mid \uE001#b\uE002";
        ChatTextEditor.IsInsideToken(text, 5).Should().BeFalse("两 Token 之间的用户文本是安全区");
        ChatTextEditor.IsInsideToken(text, text.Length - 2).Should().BeTrue("落在第二个 Token 内部");
    }
}