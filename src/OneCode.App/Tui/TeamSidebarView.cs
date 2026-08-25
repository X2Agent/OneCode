namespace OneCode.App.Tui;

/// <summary>
/// Right-docked TEAM run sidebar — a display-only status board for the active
/// team run: phase header, task list, clarification decisions, aggregated file
/// changes, quality gates, and key milestones.
///
/// 纯显示面板：不注册任何键位、不显示操作提示，所有用户交互都发生在左侧
/// 对话列（澄清向导 / 审批卡）。内容是对已确定事实的只读投影，由
/// <see cref="ReplShell"/> 消费 Team 事件驱动刷新（见 ReplShell.TeamSidebar.cs）。
/// Chrome inherited from <see cref="SidebarViewBase"/>.
/// </summary>
internal sealed class TeamSidebarView : SidebarViewBase
{
    private const string HeaderGlyphConst = "\U0001f6e0";

    public TeamSidebarView(IApplication app, Action widthChanged, Action dragEnded)
        : base(app, widthChanged, dragEnded, "团队")
    {
    }

    protected override string HeaderGlyph => HeaderGlyphConst;

    protected override Attribute HeaderAttribute =>
        new(TuiPalette.ModeTeamFg, TuiPalette.BgTerminalHeader);
}
