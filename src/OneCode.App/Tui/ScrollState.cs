namespace OneCode.App.Tui;

// 滚动状态管理——从 MessageListView 提取。
// 封装滚动偏移、自动滚动、滚动到底部标记，以及所有滚动操作。
// 通过回调与宿主 View 交互（视口高度、行数、滚动事件、重绘请求）。

internal sealed class ScrollState
{
    private int _scrollOffset;
    private bool _autoScroll = true;
    private bool _needsScrollToBottom;

    private readonly Func<int> _getViewportHeight;
    private readonly Func<int> _getLineCount;
    private readonly Action<int>? _onScrolled;
    private readonly Action? _onNeedsDraw;

    public int ScrollOffset => _scrollOffset;
    public bool AutoScroll => _autoScroll;
    public bool NeedsScrollToBottom => _needsScrollToBottom;

    public ScrollState(
        Func<int> getViewportHeight,
        Func<int> getLineCount,
        Action<int>? onScrolled = null,
        Action? onNeedsDraw = null)
    {
        _getViewportHeight = getViewportHeight;
        _getLineCount = getLineCount;
        _onScrolled = onScrolled;
        _onNeedsDraw = onNeedsDraw;
    }

    /// <summary>滚动到指定行（居中显示），用于搜索跳转。</summary>
    public void ScrollToLine(int lineIdx)
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var maxOffset = Math.Max(0, _getLineCount() - visibleLines);
        _scrollOffset = Math.Clamp(lineIdx - visibleLines / 2, 0, maxOffset);
        _autoScroll = false;
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
    }

    public void ScrollToBottom()
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var newOffset = Math.Max(0, _getLineCount() - visibleLines);
        if (newOffset != _scrollOffset)
        {
            _scrollOffset = newOffset;
            _onScrolled?.Invoke(_scrollOffset);
        }
        _needsScrollToBottom = false;
    }

    public void ScrollUp(int lines = 3)
    {
        _autoScroll = false;
        _scrollOffset = Math.Max(0, _scrollOffset - lines);
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
        _onNeedsDraw?.Invoke();
    }

    public void ScrollDown(int lines = 3)
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var maxOffset = Math.Max(0, _getLineCount() - visibleLines);
        _scrollOffset = Math.Min(maxOffset, _scrollOffset + lines);
        if (_scrollOffset >= maxOffset)
            _autoScroll = true;
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
        _onNeedsDraw?.Invoke();
    }

    public void PageUp()
    {
        _autoScroll = false;
        _scrollOffset = Math.Max(0, _scrollOffset - Math.Max(1, _getViewportHeight()));
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
        _onNeedsDraw?.Invoke();
    }

    public void PageDown()
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var maxOffset = Math.Max(0, _getLineCount() - visibleLines);
        _scrollOffset = Math.Min(maxOffset, _scrollOffset + Math.Max(1, visibleLines));
        if (_scrollOffset >= maxOffset)
            _autoScroll = true;
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
        _onNeedsDraw?.Invoke();
    }

    /// <summary>Home 键：滚动到顶部。</summary>
    public void ScrollToTop()
    {
        _scrollOffset = 0;
        _autoScroll = false;
        _needsScrollToBottom = false;
        _onScrolled?.Invoke(_scrollOffset);
        _onNeedsDraw?.Invoke();
    }

    /// <summary>End 键：启用自动滚动并滚动到底部。</summary>
    public void ScrollToEnd()
    {
        _autoScroll = true;
        ScrollToBottom();
        _onNeedsDraw?.Invoke();
    }

    /// <summary>由行变更方法调用：当 AutoScroll 为 true 时标记需要滚动到底部。</summary>
    public void RequestScrollToBottomIfAutoScroll() => _needsScrollToBottom = _autoScroll;

    /// <summary>重置所有滚动状态（用于 Clear）。</summary>
    public void Reset()
    {
        _scrollOffset = 0;
        _autoScroll = true;
        _needsScrollToBottom = false;
    }
}
