namespace OneCode.App.Tui;

/// <summary>Confirms whether the user trusts the current workspace.</summary>
public sealed class TrustOverlay : ResultOverlay<bool>
{
    private readonly string _cwd;
    private readonly bool _isHomeDir;
    private Button? _yesButton;

    protected override View? InitialFocusView => _yesButton;

    public TrustOverlay(string cwd, bool isHomeDir)
        : base("  信任工作区  (Esc 关闭)", preferredWidth: 72, preferredHeight: 14)
    {
        _cwd = cwd;
        _isHomeDir = isHomeDir;
        BuildUi();
    }

    protected override bool GetDismissedResult(OverlayCloseReason reason) => false;

    private void BuildUi()
    {
        var dirLabel = new Label
        {
            Text = _cwd,
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill(TuiSpacing.OverlayContentX),
            TextAlignment = Alignment.Center,
            CanFocus = false,
        };
        dirLabel.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        var warningText = new Label
        {
            Text =
                "快速安全检查：这是你创建的项目或你信任的项目吗？\n" +
                "（比如你自己的代码、知名开源项目、或你团队的工作）。\n" +
                "如果不是，请先花点时间查看这个文件夹里有什么。",
            X = TuiSpacing.OverlayContentX,
            Y = Pos.Bottom(dirLabel) + TuiSpacing.Xs,
            Width = Dim.Fill(TuiSpacing.OverlayContentX),
            Height = 3,
            TextAlignment = Alignment.Center,
            CanFocus = false,
        };
        warningText.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        var accessNote = new Label
        {
            Text = _isHomeDir
                ? "OneCode 将能够读取、编辑和执行用户目录中的文件。"
                : "OneCode 将能够读取、编辑和执行此处的文件。",
            X = TuiSpacing.OverlayContentX,
            Y = Pos.Bottom(warningText) + TuiSpacing.Xs,
            Width = Dim.Fill(TuiSpacing.OverlayContentX),
            TextAlignment = Alignment.Center,
            CanFocus = false,
        };
        accessNote.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        var noButton = CreateButton($"{TuiGlyphs.Failed} _否，退出", Pos.AnchorEnd(14));
        noButton.Accepting += (_, _) => RequestClose(OverlayCloseReason.Cancelled);

        _yesButton = CreateButton($"{TuiGlyphs.Complete} _是，我信任此文件夹", Pos.AnchorEnd(34));
        _yesButton.Accepting += (_, _) => Complete(true);

        Add(dirLabel, warningText, accessNote, _yesButton, noButton);
    }

    private static Button CreateButton(string text, Pos x)
    {
        var button = new Button
        {
            Text = text,
            X = x,
            Y = Pos.AnchorEnd(TuiSpacing.Sm),
        };
        button.SetScheme(TuiTheme.MakeButtonScheme(TuiPalette.FgPrimary, TuiPalette.BgCard, TuiPalette.BgActive));
        return button;
    }
}
