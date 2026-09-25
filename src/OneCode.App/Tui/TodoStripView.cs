using OneCode.Core.Coordinator;

namespace OneCode.App.Tui;

/// <summary>
/// 底部待办横条（内容区与 Agent 状态栏之间）：渲染 agent 的 <c>todos_*</c>
/// 清单快照（Harness <c>TodoProvider</c> 会话状态的权威投影，经
/// <see cref="OrchestrationEvent.TodoProjectionChanged"/> 到达）。
///
/// 布局选择：放在输入框上方的全宽横条而非右侧 sidebar——Plan/TEAM 侧边栏互斥，
/// 而计划执行期间最需要同时看到计划步骤（右）与待办（下）；横条也不参与
/// 侧边栏的宽度让位逻辑，对话列宽度不受影响（仅高度让位，见
/// <c>ReplShell.ApplyTodoStripLayout</c>）。
///
/// 空列表时整体隐藏，不占行。条目超过 <see cref="MaxItems"/> 时末行汇总剩余数。
/// </summary>
internal sealed class TodoStripView : View
{
    /// <summary>横条最多展示的条目数（不含头部行与汇总行），防止清单挤压对话区。</summary>
    public const int MaxItems = 4;

    private IReadOnlyList<TodoListItem> _items = [];

    public TodoStripView()
    {
        CanFocus = false;
        TabStop = TabBehavior.NoStop;
    }

    /// <summary>用新快照替换内容；空快照由调用方负责隐藏（见 <c>ReplShell</c>）。</summary>
    public void Update(IReadOnlyList<TodoListItem> items)
    {
        _items = items;
        SetNeedsDraw();
    }

    /// <summary>当前快照占用的行数（头部 1 + 条目 + 可选汇总 1）；空清单为 0。</summary>
    public int RequiredRows
    {
        get
        {
            if (_items.Count == 0)
                return 0;

            var itemRows = Math.Min(_items.Count, MaxItems);
            var overflowRow = _items.Count > MaxItems ? 1 : 0;
            return 1 + itemRows + overflowRow;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0 || _items.Count == 0)
            return true;

        var remaining = _items.Count(item => !item.IsComplete);

        // 头部：反色条，与 AgentStatusBar 的曲面底色一致，视觉上属于同一层状态区。
        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.Accent, TuiPalette.BgSurface));
        var header = $" {TuiGlyphs.BrandMark} 待办 ({_items.Count - remaining}/{_items.Count})";
        AddStr(PadOrTruncate(header, width));

        var row = 1;
        for (var i = 0; i < _items.Count && row <= MaxItems; i++, row++)
        {
            var item = _items[i];
            Move(0, row);
            if (item.IsComplete)
            {
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgSurface));
                AddStr(PadOrTruncate($"  {TuiGlyphs.Complete} #{item.Id} {item.Title}", width));
            }
            else
            {
                SetAttribute(new Attribute(TuiPalette.FgPrimary, TuiPalette.BgSurface));
                AddStr(PadOrTruncate($"  {TuiGlyphs.Pending} #{item.Id} {item.Title}", width));
            }
        }

        if (_items.Count > MaxItems)
        {
            Move(0, row);
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgSurface));
            AddStr(PadOrTruncate($"  {TuiGlyphs.Ellipsis} 其余 {_items.Count - MaxItems} 项", width));
        }

        return true;
    }

    /// <summary>按显示宽度补齐或截断（窄终端下标题优先保证行结构完整）。</summary>
    private static string PadOrTruncate(string text, int width)
    {
        if (text.Length >= width)
            return text[..Math.Max(0, width)];

        return text + new string(' ', width - text.Length);
    }
}
