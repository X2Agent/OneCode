using System.Collections.ObjectModel;

namespace OneCode.App.Tui;

/// <summary>Lets the user pick a previous session to resume.</summary>
public sealed class ResumeChooserOverlay : ResultOverlay<string?>
{
    private readonly ListView _listView;
    private readonly List<SessionEntry> _entries = [];

    protected override View? InitialFocusView => _listView;

    public ResumeChooserOverlay(IReadOnlyList<SessionEntry> sessions, string? projectPath = null)
        : base("  恢复会话  (Esc 关闭)", preferredWidth: 80, preferredHeight: 22)
    {
        _ = projectPath;
        PreferredWidth = 80;
        PreferredHeight = Math.Clamp(sessions.Count + 9, 12, 26);
        _entries.AddRange(sessions);

        var label = new Label
        {
            Text = "选择要恢复的会话（Enter 恢复，N 新建会话）",
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill(TuiSpacing.Md),
            CanFocus = false,
        };
        label.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        _listView = new ListView
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY + TuiSpacing.Sm,
            Width = Dim.Fill(TuiSpacing.Md),
            Height = Dim.Fill(TuiSpacing.Md),
            CanFocus = true,
        };
        _listView.SetSource(new ObservableCollection<string>(_entries.Select(e => e.DisplayText).ToList()));
        _listView.SetScheme(TuiTheme.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _listView.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                CompleteWithSelected();
                key.Handled = true;
            }
        };

        var newButton = CreateButton($"{TuiGlyphs.Pending} _新会话", Pos.AnchorEnd(28));
        newButton.Accepting += (_, _) => RequestClose(OverlayCloseReason.Cancelled);

        var resumeButton = CreateButton($"{TuiGlyphs.Complete} _恢复", Pos.AnchorEnd(14));
        resumeButton.Accepting += (_, _) => CompleteWithSelected();

        Add(label, _listView, newButton, resumeButton);
    }

    protected override string? GetDismissedResult(OverlayCloseReason reason) => null;

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.N || kb == Key.N.WithShift)
        {
            RequestClose(OverlayCloseReason.Cancelled);
            return true;
        }

        return base.OnKeyDown(kb);
    }

    private void CompleteWithSelected()
    {
        var index = _listView.SelectedItem ?? -1;
        if (index >= 0 && index < _entries.Count)
            Complete(_entries[index].Id);
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

/// <summary>Display entry for a resumable session.</summary>
public sealed record SessionEntry(string Id, string Name, string Model, int MessageCount, DateTimeOffset LastActivity)
{
    public string DisplayText =>
        $"{Name,-30} {Model,-20} {MessageCount,4} 条  {LastActivity:MM-dd HH:mm}";
}
