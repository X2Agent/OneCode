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

    // 回归防护：原子删除 Token 只派发一次 ContentsChanged。
    // Document.Remove 会同步触发 OnDocumentChanged → ContentsChanged，若再手动派发
    // 一次，OnInputTextChanged / PruneMissing / 补全逻辑会重复执行两次。
    [Fact]
    public void TokenBackspace_AtomicDelete_FiresContentsChangedExactlyOnce()
    {
        var editor = new ChatTextEditor();
        editor.Text = Text;
        editor.InsertionPoint = TokenEnd + 1; // \uE002 之后一位

        var fires = 0;
        editor.ContentsChanged += (_, _) => fires++;

        editor.DispatchTokenKey(Terminal.Gui.Input.Key.Backspace).Should().BeTrue();

        editor.Text.Should().Be("abcd");
        fires.Should().Be(1, "原子删除 Token 只应派发一次 ContentsChanged");
    }

    [Fact]
    public void TokenDelete_AtomicDelete_FiresContentsChangedExactlyOnce()
    {
        var editor = new ChatTextEditor();
        editor.Text = Text;
        editor.InsertionPoint = TokenStart; // \uE001 处

        var fires = 0;
        editor.ContentsChanged += (_, _) => fires++;

        editor.DispatchTokenKey(Terminal.Gui.Input.Key.Delete).Should().BeTrue();

        editor.Text.Should().Be("abcd");
        fires.Should().Be(1, "原子删除 Token 只应派发一次 ContentsChanged");
    }

    // 图片占位符与文本折叠共用 PUA 定界符，因此同样获得原子删除语义——
    // 半截删除不会再留下孤立的 "[Image #"。
    [Fact]
    public void ImageTag_IsAtomicOnBackspace()
    {
        var editor = new ChatTextEditor();
        editor.Text = "see \uE001[Image #1]\uE002 now";
        editor.InsertionPoint = 16; // \uE002 之后一位

        editor.DispatchTokenKey(Terminal.Gui.Input.Key.Backspace).Should().BeTrue();

        editor.Text.Should().Be("see  now");
    }

    // 落点计算等价于 Editor 原生行为（自行计算只为在落点生效前检查 Token 内部）。
    [Fact]
    public void VerticalSameColumn_MovesToSameColumnAcrossLines()
    {
        const string text = "abc\nde\nfghi"; // 行起点：0 / 4 / 7

        ChatTextEditor.VerticalSameColumn(text, 1, forward: true).Should().Be(5);
        ChatTextEditor.VerticalSameColumn(text, 5, forward: true).Should().Be(8);
        ChatTextEditor.VerticalSameColumn(text, 8, forward: true).Should().BeNull("末行无下一行");

        ChatTextEditor.VerticalSameColumn(text, 8, forward: false).Should().Be(5);
        ChatTextEditor.VerticalSameColumn(text, 5, forward: false).Should().Be(1);
        ChatTextEditor.VerticalSameColumn(text, 1, forward: false).Should().BeNull("首行无上一行");
    }

    [Fact]
    public void VerticalSameColumn_ClampsToShorterLineLength()
    {
        const string text = "abcdef\nxy"; // 下一行仅 2 列

        ChatTextEditor.VerticalSameColumn(text, 5, forward: true).Should().Be(text.Length);
    }

    [Fact]
    public void WordBoundary_MovesByWordAndStopsAtEdges()
    {
        const string text = "foo bar_baz qux"; // 长度 15

        ChatTextEditor.WordBoundary(text, 0, forward: true).Should().Be(3);
        ChatTextEditor.WordBoundary(text, 3, forward: true).Should().Be(11);
        ChatTextEditor.WordBoundary(text, 11, forward: true).Should().Be(15);
        ChatTextEditor.WordBoundary(text, 15, forward: true).Should().BeNull();

        ChatTextEditor.WordBoundary(text, 15, forward: false).Should().Be(12);
        ChatTextEditor.WordBoundary(text, 3, forward: false).Should().Be(0);
        ChatTextEditor.WordBoundary(text, 0, forward: false).Should().BeNull();
    }
}