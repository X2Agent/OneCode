using System.Collections.ObjectModel;
using OneCode.Core.Keybindings;

namespace OneCode.Tui;

/// <summary>Lets the user pick a previous session to resume.</summary>
public sealed class ResumeChooserOverlay : ResultOverlay<string?>
{
    private readonly ListView _listView;
    private readonly Button _newButton;
    private readonly Button _resumeButton;
    private readonly List<SessionEntry> _entries = [];
    private readonly KeybindingResolver _keyResolver;
    private readonly KeybindingContextManager _keyContextManager;

    internal ListView ListView => _listView;
    internal Button NewButton => _newButton;
    internal Button ResumeButton => _resumeButton;

    protected override View? InitialFocusView => _listView;

    public ResumeChooserOverlay(
        IReadOnlyList<SessionEntry> sessions,
        string? projectPath = null,
        KeybindingResolver? keyResolver = null,
        KeybindingContextManager? keyContextManager = null)
        : base("  恢复会话  (Esc 关闭)", preferredWidth: 80, preferredHeight: 22)
    {
        _ = projectPath;
        _keyResolver = keyResolver ?? new KeybindingResolver();
        if (keyResolver is null)
            _keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        _keyContextManager = keyContextManager ?? new KeybindingContextManager();

        PreferredWidth = 80;
        PreferredHeight = Math.Clamp(sessions.Count + 9, 12, 26);
        _entries.AddRange(sessions);

        var label = new Label
        {
            Text = "选择要恢复的会话（Enter 恢复，N 新建会话，Tab 切换焦点）",
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill(TuiSpacing.Md),
            CanFocus = false,
        };
        label.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        _listView = new ListView
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY + TuiSpacing.Sm,
            Width = Dim.Fill(TuiSpacing.Md),
            Height = Dim.Fill(TuiSpacing.Md),
            CanFocus = true,
        };
        _listView.SetSource(new ObservableCollection<string>(_entries.Select(e => e.DisplayText).ToList()));
        if (_entries.Count > 0)
            _listView.SelectedItem = 0;
        _listView.SetScheme(TuiStyles.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _listView.KeyDown += (_, key) =>
        {
            if (HandleSelectorKey(key))
                key.Handled = true;
        };

        _newButton = CreateButton($"{TuiGlyphs.Pending} _新会话", Pos.AnchorEnd(28));
        _newButton.Accepting += (_, _) => RequestClose(OverlayCloseReason.Cancelled);

        _resumeButton = CreateButton($"{TuiGlyphs.Complete} _恢复", Pos.AnchorEnd(14));
        _resumeButton.Accepting += (_, _) => CompleteWithSelected();

        Add(label, _listView, _newButton, _resumeButton);
    }

    protected override string? GetDismissedResult(OverlayCloseReason reason) => null;

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.Tab)
            return CycleTabStop(forward: true);
        if (kb == Key.Tab.WithShift)
            return CycleTabStop(forward: false);

        if (kb == Key.N || kb == Key.N.WithShift)
        {
            RequestClose(OverlayCloseReason.Cancelled);
            return true;
        }

        if (MostFocused == _listView && HandleSelectorKey(kb))
            return true;

        return base.OnKeyDown(kb);
    }

    private View[] TabStops => [_listView, _newButton, _resumeButton];

    private bool CycleTabStop(bool forward)
    {
        var stops = TabStops;
        var index = Array.IndexOf(stops, MostFocused);
        var next = forward
            ? (index + 1) % stops.Length
            : (index <= 0 ? stops.Length - 1 : index - 1);
        stops[next].SetFocus();
        return true;
    }

    private bool HandleSelectorKey(Key kb)
    {
        var contexts = new HashSet<string>(_keyContextManager.ActiveContexts, StringComparer.Ordinal)
        {
            KeybindingDefaults.ContextSelector,
        };

        switch (TuiKeyAdapter.ResolveAction(kb, _keyResolver, contexts))
        {
            case KeybindingDefaults.ActionSelectorPrevious:
                if (_entries.Count > 0)
                {
                    var current = _listView.SelectedItem ?? 0;
                    if (current > 0)
                        _listView.SelectedItem = current - 1;
                }
                return true;

            case KeybindingDefaults.ActionSelectorNext:
                if (_entries.Count > 0)
                {
                    var current = _listView.SelectedItem ?? 0;
                    if (current < _entries.Count - 1)
                        _listView.SelectedItem = current + 1;
                }
                return true;

            case KeybindingDefaults.ActionSelectorConfirm:
                CompleteWithSelected();
                return true;

            case KeybindingDefaults.ActionSelectorDismiss:
                RequestClose(OverlayCloseReason.Escape);
                return true;

            default:
                return false;
        }
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
        button.SetScheme(TuiStyles.MakeButtonScheme(TuiPalette.FgPrimary, TuiPalette.BgCard, TuiPalette.BgActive));
        return button;
    }
}

/// <summary>Display entry for a resumable session.</summary>
public sealed record SessionEntry(string Id, string Title, string Model, string Mode, int MessageCount, DateTimeOffset LastActivity)
{
    /// <summary>标题列宽度（显示列数）：CJK 标题按字符数截断会占双倍宽度，把时间列挤出 overlay。</summary>
    private const int TitleColumnWidth = 26;

    public string DisplayText =>
        $"{TruncateTitle()} {Model,-18} {Mode,-6} {MessageCount,4} 条  {LastActivity:MM-dd HH:mm}";

    private string TruncateTitle()
    {
        var truncated = TextWidthHelper.TruncateByWidth(Title, TitleColumnWidth);
        // 格式化对齐按字符数计——按显示宽度右补齐到固定列数，CJK 每字符占 2 列才不会错位。
        var padding = Math.Max(0, TitleColumnWidth - TextWidthHelper.GetDisplayWidth(truncated));
        return truncated + new string(' ', padding);
    }
}
