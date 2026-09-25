using OneCode.Core.Coordinator;

namespace OneCode.App.Tui;

/// <summary>
/// 待办横条（<see cref="TodoStripView"/>）的宿主逻辑：快照进入、显示/隐藏与
/// 内容区高度让位。与 Plan/TEAM 侧边栏（右停靠、互斥、Ctrl+G 切换）相互独立——
/// 横条是全宽底部状态带，不参与侧边栏互斥。
/// </summary>
public sealed partial class ReplShell
{
    private readonly TodoStripView _todoStrip;

    /// <summary>
    /// 用权威快照更新横条。空列表隐藏横条并归还对话区高度；
    /// 非空时按 <see cref="TodoStripView.RequiredRows"/> 让位。
    /// </summary>
    public void ShowTodoPanel(IReadOnlyList<TodoListItem> items)
    {
        _todoStrip.Update(items);
        ApplyTodoStripLayout();
    }

    /// <summary>清空待办横条并隐藏（会话切换/关闭时调用）。</summary>
    public void ClearTodoPanel()
    {
        _todoStrip.Update([]);
        ApplyTodoStripLayout();
    }

    private void ApplyTodoStripLayout()
    {
        var rows = _todoStrip.RequiredRows;
        _todoStripRows = rows;
        _todoStrip.Visible = rows > 0;
        if (rows > 0)
        {
            _todoStrip.Height = rows;
            _todoStrip.Y = Pos.AnchorEnd(TuiSpacing.ContentZoneReservedBottom + rows);
        }

        // 横条从内容区底部"借"行：内容区下边界随横条行数下移，
        // 对话列宽度不变（仅高度变化，不需要按新宽度重排换行）。
        _contentZone.Height = Dim.Fill(TuiSpacing.ContentZoneReservedBottom + rows);
        _todoStrip.SetNeedsDraw();
    }
}
