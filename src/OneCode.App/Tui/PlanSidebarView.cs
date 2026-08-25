namespace OneCode.App.Tui;

/// <summary>
/// Right-docked plan sidebar — replaces the in-transcript plan card.
///
/// The full plan (title, document path, markdown, step list, phase state)
/// renders here instead of the conversation flow, keeping the chat column clean.
/// Chrome (header line, separator drag handle, width clamping, display-only
/// contract) is inherited from <see cref="SidebarViewBase"/>.
///
/// Visibility is driven by <see cref="ReplShell"/>: auto-shown when a plan exists
/// (first plan submission), auto-hidden when the plan is cleared. Ctrl+G toggles
/// manually (KeybindingDefaults.ActionChatTogglePlanPanel).
/// </summary>
internal sealed class PlanSidebarView : SidebarViewBase
{
    private const string HeaderGlyphConst = "\U0001f4cb";

    public PlanSidebarView(IApplication app, Action widthChanged, Action dragEnded)
        : base(app, widthChanged, dragEnded, "计划")
    {
    }

    protected override string HeaderGlyph => HeaderGlyphConst;

    protected override Attribute HeaderAttribute =>
        new(TuiPalette.ModePlanFg, TuiPalette.BgTerminalHeader);
}
