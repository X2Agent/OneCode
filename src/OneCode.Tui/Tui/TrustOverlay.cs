namespace OneCode.Tui;

/// <summary>Confirms whether the user trusts the current workspace.</summary>
public sealed class TrustOverlay : ResultOverlay<bool>
{
    private readonly string _cwd;
    private readonly bool _isHomeDir;
    private readonly Button _yesButton;
    private readonly Button _noButton;

    internal Button YesButton => _yesButton;
    internal Button NoButton => _noButton;

    protected override View? InitialFocusView => _yesButton;

    public TrustOverlay(string cwd, bool isHomeDir)
        : base("  信任工作区  (Esc 关闭)", preferredWidth: 72, preferredHeight: 14)
    {
        _cwd = cwd;
        _isHomeDir = isHomeDir;
        _yesButton = TuiButtons.Create($"{TuiGlyphs.Complete} _是，我信任此文件夹");
        _noButton = TuiButtons.Create($"{TuiGlyphs.Failed} _否，退出");
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
        dirLabel.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

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
        warningText.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

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
        accessNote.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        _yesButton.Accepting += (_, _) => Complete(true);
        _noButton.Accepting += (_, _) => RequestClose(OverlayCloseReason.Cancelled);

        Add(dirLabel, warningText, accessNote, TuiButtons.CreateRightAlignedRow(_yesButton, _noButton));
    }

    protected override bool OnKeyDown(Key kb)
    {
        if (HandleNavigationKey(kb))
            return true;

        return base.OnKeyDown(kb);
    }

    private bool HandleNavigationKey(Key kb)
    {
        if (kb == Key.Tab || kb == Key.CursorRight)
            return CycleTabStop(forward: true);
        if (kb == Key.Tab.WithShift || kb == Key.CursorLeft)
            return CycleTabStop(forward: false);

        return false;
    }

    private bool CycleTabStop(bool forward)
    {
        View[] stops = [_yesButton, _noButton];
        var index = Array.IndexOf(stops, MostFocused);
        var next = forward
            ? (index + 1) % stops.Length
            : (index <= 0 ? stops.Length - 1 : index - 1);
        stops[next].SetFocus();
        return true;
    }
}
