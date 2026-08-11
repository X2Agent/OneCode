namespace OneCode.App.Tui;

/// <summary>
/// Diff detail overlay — shown when pressing Enter on a file in ReviewOverlay.
/// Near-fullscreen for readable hunk review; Esc returns to the file list.
/// </summary>
public sealed class DiffDetailOverlay : CenteredOverlay
{
    private readonly DiffView _diffView;
    private readonly Label _emptyHint;

    public override OverlayLayoutMode LayoutMode => OverlayLayoutMode.Fill;

    protected override View? InitialFocusView => _diffView.Visible ? _diffView : _emptyHint;

    public DiffDetailOverlay(IApplication app, string filePath, string diffText)
        : base($"  差异详情 — {filePath}  (↑↓/PgUp/PgDn 滚动 · Esc 返回)  ", preferredWidth: 80, preferredHeight: 28)
    {
        _ = app;
        var hasDiff = !string.IsNullOrWhiteSpace(diffText);

        _diffView = new DiffView
        {
            X = TuiSpacing.OverlayContentX - 1,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill() - (TuiSpacing.OverlayContentX - 1),
            Height = Dim.Fill() - 2,
            Visible = hasDiff,
        };

        _emptyHint = new Label
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill() - 4,
            Height = 2,
            Text = $"（{filePath} 无文本差异 — 可能是二进制文件或仅有 mode 变更）",
            CanFocus = true,
            Visible = !hasDiff,
        };
        _emptyHint.SetScheme(TuiTheme.MakeScheme(TuiPalette.FgMuted, TuiPalette.BgCard));

        Add(_diffView, _emptyHint);
        if (hasDiff)
            _diffView.SetDiff(diffText);
    }
}
