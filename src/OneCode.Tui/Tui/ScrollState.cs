namespace OneCode.Tui;

// 滚动状态管理：封装滚动偏移、自动滚动、滚动到底部标记，以及所有滚动操作。
// 通过回调与宿主 View 交互（视口高度、行数、滚动事件、重绘请求）。

internal sealed class ScrollState(
    Func<int> getViewportHeight,
    Func<int> getLineCount,
    Action<int>? onScrolled = null,
    Action? onNeedsDraw = null)
{
    private int _scrollOffset;
    private bool _autoScroll = true;
    private bool _needsScrollToBottom;

    private readonly Func<int> _getViewportHeight = getViewportHeight;
    private readonly Func<int> _getLineCount = getLineCount;
    private readonly Action<int>? _onScrolled = onScrolled;
    private readonly Action? _onNeedsDraw = onNeedsDraw;

    public int ScrollOffset => _scrollOffset;
    public bool AutoScroll => _autoScroll;
    public bool NeedsScrollToBottom => _needsScrollToBottom;

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
        // 主动下翻与上翻同语义：未到底 = 用户在阅读（脱离贴底，内容更新保留位置）；
        // 到底 = 恢复跟随。
        _autoScroll = _scrollOffset >= maxOffset;
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
        // 与 ScrollDown 同语义：未到底脱离贴底（保留阅读位置），到底恢复跟随。
        _autoScroll = _scrollOffset >= maxOffset;
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

    /// <summary>
    /// 滚动条拖动：把偏移直接钳制到位。拖到中间脱离贴底（保留阅读位置），
    /// 拖到底恢复跟随——与 PageDown 的语义一致。
    /// </summary>
    public void ScrollToOffset(int offset)
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var maxOffset = Math.Max(0, _getLineCount() - visibleLines);
        _scrollOffset = Math.Clamp(offset, 0, maxOffset);
        _autoScroll = _scrollOffset >= maxOffset;
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

    /// <summary>恢复贴底跟随状态（Clear/Reset 之后的阅读位置保留路径用）。</summary>
    internal void SetAutoScroll(bool value) => _autoScroll = value;

    /// <summary>
    /// 整体重渲后恢复滚动位置：重渲不改变 AutoScroll 语义，
    /// 仅将偏移钳制到新行数范围内（重换行后行数会变化）。
    /// </summary>
    internal void RestoreOffset(int preferredOffset)
    {
        var visibleLines = Math.Max(1, _getViewportHeight());
        var maxOffset = Math.Max(0, _getLineCount() - visibleLines);
        _scrollOffset = Math.Clamp(preferredOffset, 0, maxOffset);
        _needsScrollToBottom = false;
    }

    /// <summary>重置所有滚动状态（用于 Clear）。</summary>
    public void Reset()
    {
        _scrollOffset = 0;
        _autoScroll = true;
        _needsScrollToBottom = false;
    }
}
