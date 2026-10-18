namespace OneCode.App.Tui;

/// <summary>
/// 滚动条几何计算与鼠标拖拽控制器：从 <see cref="MessageListView"/> 拆出，
/// 统一管理拇指尺寸/位置计算、拖拽状态机与滚动偏移反算。
/// </summary>
internal sealed class ScrollBarController(
    ScrollState scroll,
    IApplication? app,
    Func<int> getLineCount,
    Func<int> getViewportHeight)
{
    private bool _isDragging;
    private int _grabOffset;

    /// <summary>当前是否处于滚动条拖拽状态。</summary>
    public bool IsDragging => _isDragging;

    /// <summary>在滚动条列按下时启动拖拽。</summary>
    public void BeginDrag(View owner, int pressY)
    {
        var lineCount = getLineCount();
        var viewportHeight = getViewportHeight();
        var (thumbSize, thumbPos) = ComputeGeometry(lineCount, viewportHeight, scroll.ScrollOffset);
        if (thumbSize == 0)
            return;

        _grabOffset = pressY >= thumbPos && pressY < thumbPos + thumbSize
            ? pressY - thumbPos
            : thumbSize / 2;
        if (pressY < thumbPos || pressY >= thumbPos + thumbSize)
            ApplyDrag(pressY);

        _isDragging = true;
        app?.Mouse?.GrabMouse(owner);
    }

    /// <summary>拖拽移动时更新滚动偏移。</summary>
    public void ApplyDrag(int dragY)
    {
        var lineCount = getLineCount();
        var viewportHeight = getViewportHeight();
        var (thumbSize, _) = ComputeGeometry(lineCount, viewportHeight, scroll.ScrollOffset);
        if (thumbSize == 0)
            return;

        var thumbTop = dragY - _grabOffset;
        scroll.ScrollToOffset(ScrollOffsetFromThumbTop(thumbTop, thumbSize, viewportHeight, lineCount));
    }

    /// <summary>鼠标释放时结束拖拽。</summary>
    public void EndDrag()
    {
        _isDragging = false;
        app?.Mouse?.UngrabMouse();
    }

    /// <summary>
    /// 滚动条几何：拇指尺寸与顶部行。纯函数——绘制与拖动命中判断共用同一份计算。
    /// 无滚动需求（内容不超视口）时 thumbSize 为 0。
    /// </summary>
    public static (int ThumbSize, int ThumbPos) ComputeGeometry(
        int lineCount, int viewportHeight, int scrollOffset)
    {
        if (lineCount <= 0 || viewportHeight <= 0 || lineCount <= viewportHeight)
            return (0, 0);

        var thumbSize = Math.Max(1, viewportHeight * viewportHeight / lineCount);
        var scrollRange = lineCount - viewportHeight;
        var thumbPos = (int)((float)scrollOffset / scrollRange * (viewportHeight - thumbSize));
        return (thumbSize, Math.Clamp(thumbPos, 0, viewportHeight - thumbSize));
    }

    /// <summary>拇指顶部行 → 滚动偏移（拖动的反算）。纯函数。</summary>
    public static int ScrollOffsetFromThumbTop(int thumbTop, int thumbSize, int viewportHeight, int lineCount)
    {
        if (lineCount <= viewportHeight || viewportHeight <= 0)
            return 0;
        var scrollRange = lineCount - viewportHeight;
        var trackRange = viewportHeight - thumbSize;
        if (trackRange <= 0)
            return 0;
        return Math.Clamp((int)Math.Round((float)thumbTop / trackRange * scrollRange), 0, scrollRange);
    }
}
