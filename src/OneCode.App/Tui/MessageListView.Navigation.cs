namespace OneCode.App.Tui;

/// <summary>
/// Transcript 导航模式支持：<see cref="OneCode.App.Transcript.TranscriptViewModel"/> 的可交互行快照
/// 来源、游标行高亮渲染状态，以及键盘路径的展开/复制动作。
/// 进出导航与 transcript:* 分发在 ReplShell（焦点切换 + 上下文 push/pop）。
/// </summary>
public sealed partial class MessageListView
{
    private int _navHighlightLine = -1;

    /// <summary>当前导航高亮行；-1 表示未激活。由 ReplShell 经 SetNavigationHighlight 维护。</summary>
    public int NavigationHighlightLine => _navHighlightLine;

    /// <summary>
    /// 可交互行（工具/思考/错误摘要行）的有序绝对行号快照。
    /// 详情行（ToolDetail/ThinkingDetail）与普通文本不算可交互行。
    /// </summary>
    public IReadOnlyList<int> GetInteractiveLineIndices()
    {
        var indices = new List<int>();
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Tag is ToolLineTag or ThinkingLineTag or ErrorLineTag)
                indices.Add(i);
        }
        return indices;
    }

    /// <summary>设置/清除导航高亮（传 -1 清除）并滚动到可见区。</summary>
    public void SetNavigationHighlight(int lineIdx)
    {
        _navHighlightLine = lineIdx;
        if (lineIdx >= 0)
            ScrollToLine(lineIdx);
        SetNeedsDraw();
    }

    /// <summary>键盘路径的展开/折叠：命中可交互块返回 true。</summary>
    public bool ToggleAt(int lineIdx) => TryToggleExpansionAt(lineIdx);

    /// <summary>
    /// 键盘路径的复制：把游标行的完整文本交给剪贴板。
    /// 对话流没有独立的代码块复制标记（鼠标复制入口同样缺位），此处按
    /// 「复制当前行」语义实现，保证每个可见行为都有键盘等价动作。
    /// </summary>
    public bool TryCopyCodeAt(int lineIdx)
    {
        if (lineIdx < 0 || lineIdx >= _lines.Count || _clipboard is null)
            return false;

        var text = _lines[lineIdx].Text;
        var copyTask = Task.Run(async () =>
        {
            try
            {
                await _clipboard.TryCopyTextAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CopyCodeAt clipboard write failed: {ex.Message}");
            }
        });

        // 纵深防御：观察任何逃逸内部 catch 的异常（确保不触发 UnobservedTaskException）。
#pragma warning disable CS4014
        copyTask.ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"Copy task faulted: {t.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
#pragma warning restore CS4014
        return true;
    }
}
