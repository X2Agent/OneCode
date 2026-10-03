using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ScrollState"/> 翻页语义守卫：一页 = 视口高度（不是一步到顶/到底），
/// 双向钳制正确，翻页清除流式贴底 pending 标记（否则首次翻页会被绘制期 snap 吞掉）。
/// </summary>
public sealed class ScrollStateTests
{
    private static ScrollState CreateState(int viewportHeight, int lineCount)
    {
        ScrollState? state = null;
        state = new ScrollState(
            () => viewportHeight,
            () => lineCount,
            onScrolled: _ => { },
            onNeedsDraw: () => { });
        return state;
    }

    [Fact]
    public void PageDown_MovesExactlyOneViewportAndClampsAtBottom()
    {
        var state = CreateState(viewportHeight: 10, lineCount: 55); // maxOffset = 45

        state.PageDown();
        state.ScrollOffset.Should().Be(10, "翻一页 = 视口高度");
        state.PageDown();
        state.ScrollOffset.Should().Be(20);
        state.PageDown();
        state.PageDown();
        state.PageDown();
        state.ScrollOffset.Should().Be(45, "连续翻页在底部钳制，不越过");
    }

    [Fact]
    public void PageUp_MovesExactlyOneViewportAndClampsAtTop()
    {
        var state = CreateState(viewportHeight: 10, lineCount: 55);
        state.ScrollToEnd();
        state.AutoScroll.Should().BeTrue();

        state.PageUp();
        state.ScrollOffset.Should().Be(35, "从底部上翻一页");
        state.PageUp();
        state.ScrollOffset.Should().Be(25);
        state.PageUp();
        state.PageUp();
        state.PageUp();
        state.ScrollOffset.Should().Be(0, "连续上翻在顶部钳制，不越过");
        state.AutoScroll.Should().BeFalse("上翻后脱离贴底跟随");
    }

    [Fact]
    public void PageUp_ClearsPendingScrollToBottomFlag()
    {
        // 流式场景：贴底跟随下行追加把 pending snap 标记置位；若翻页不清除，
        // 第一次翻页会在绘制期被拉回底部（表现为"按了没反应"）。
        var state = CreateState(viewportHeight: 10, lineCount: 55);
        state.ScrollToEnd();
        state.RequestScrollToBottomIfAutoScroll();
        state.NeedsScrollToBottom.Should().BeTrue();

        state.PageUp();

        state.NeedsScrollToBottom.Should().BeFalse("翻页必须撤销 pending snap");
        state.ScrollOffset.Should().Be(35);
    }

    [Fact]
    public void PageDown_ReachingBottom_ReenablesAutoScroll()
    {
        var state = CreateState(viewportHeight: 10, lineCount: 55);
        state.ScrollToEnd();
        state.PageUp();
        state.PageUp();
        state.AutoScroll.Should().BeFalse();
        state.ScrollOffset.Should().Be(25);

        state.PageDown();
        state.AutoScroll.Should().BeFalse("未到底不恢复跟随");
        state.ScrollOffset.Should().Be(35);
        state.PageDown();
        state.AutoScroll.Should().BeTrue("到达底部恢复贴底跟随");
        state.ScrollOffset.Should().Be(45);
    }

    [Fact]
    public void PageDown_WithShortContent_ClampsToZeroOffset()
    {
        // 内容不足一屏：无滚动空间，翻页保持 0（数学边界，非 bug）。
        var state = CreateState(viewportHeight: 10, lineCount: 8);

        state.PageDown();

        state.ScrollOffset.Should().Be(0);
    }
}
