using System.Collections.ObjectModel;
using OneCode.Core.Keybindings;

namespace OneCode.Tui;

/// <summary>
/// Review overlay. Triggered by <c>/diff</c> (no args).
/// File list with diff stats + hunk navigation.
/// ↑/↓ select file, Enter open, Esc returns.
///
/// Diff data is supplied by the caller (typically <see cref="IGitHelper"/>).
/// </summary>
public sealed class ReviewOverlay : CenteredOverlay
{
    private readonly List<ReviewFileEntry> _files;
    private readonly ListView _listView;
    private readonly KeybindingResolver _keyResolver;
    private readonly KeybindingContextManager _keyContextManager;

    public event Action<ReviewFileEntry>? FileSelected;

    internal ListView ListView => _listView;

    protected override View? InitialFocusView => _listView;

    public ReviewOverlay(
        IReadOnlyList<ReviewFileEntry>? files = null,
        KeybindingResolver? keyResolver = null,
        KeybindingContextManager? keyContextManager = null)
        : base($"  代码审查  ({TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} 选择 · Enter 查看 · Esc 关闭)  ", preferredWidth: 80, preferredHeight: 18)
    {
        _keyResolver = keyResolver ?? new KeybindingResolver();
        if (keyResolver is null)
            _keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        _keyContextManager = keyContextManager ?? new KeybindingContextManager();

        // Width scales via OverlayHost (up to 70% of terminal); height fits the file list.
        // Soft ceiling of 60 rows — host clamps further to 75% of viewport on resize.
        PreferredWidth = 80;
        _files = (files ?? []).ToList();
        PreferredHeight = Math.Clamp(_files.Count + 7, 12, 60);

        var header = new Label
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = PreferredWidth - 6,
            Height = 1,
            Text = _files.Count > 0 ? $"变更文件 ({_files.Count})" : "变更文件",
            CanFocus = false,
        };
        header.SetScheme(TuiStyles.MakeScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        _listView = new ListView
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY + 2,
            Width = Dim.Fill() - 4,
            Height = Dim.Fill() - 3,
            CanFocus = true,
        };
        _listView.SetScheme(TuiStyles.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _listView.SetSource(new ObservableCollection<string>(FormatEntries()));

        // Default select first item
        if (_files.Count > 0)
            _listView.SelectedItem = 0;

        _listView.KeyDown += (_, key) =>
        {
            if (HandleSelectorKey(key))
                key.Handled = true;
        };

        Add(header, _listView);
    }

    protected override bool OnKeyDown(Key kb)
    {
        if (HandleSelectorKey(kb))
            return true;

        return base.OnKeyDown(kb);
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
                if (_files.Count > 0)
                {
                    var current = _listView.SelectedItem ?? 0;
                    if (current > 0)
                        _listView.SelectedItem = current - 1;
                }
                return true;

            case KeybindingDefaults.ActionSelectorNext:
                if (_files.Count > 0)
                {
                    var current = _listView.SelectedItem ?? 0;
                    if (current < _files.Count - 1)
                        _listView.SelectedItem = current + 1;
                }
                return true;

            case KeybindingDefaults.ActionSelectorConfirm:
                if (_files.Count > 0)
                    InvokeFileSelected();
                return true;

            case KeybindingDefaults.ActionSelectorDismiss:
                if (SuperView is OverlayHost host)
                    return host.HandleEsc();
                return false;

            default:
                return false;
        }
    }


    private void InvokeFileSelected()
    {
        var idx = _listView.SelectedItem ?? 0;
        if (idx >= 0 && idx < _files.Count)
            FileSelected?.Invoke(_files[idx]);
    }

    private List<string> FormatEntries()
    {
        if (_files.Count == 0)
        {
            return new List<string>
            {
                "（未检测到 Git 变更文件）",
                "",
                "提示：",
                "  · 确认当前目录是 Git 仓库",
                "  · 使用 git status 查看工作区状态",
                "  · 未暂存和已暂存的变更都会显示在此处",
            };
        }

        var result = new List<string>(_files.Count);
        foreach (var f in _files)
            result.Add($"📄 {f.Path,-40} +{f.Added,-4} -{f.Removed,-4} {f.Status}");
        return result;
    }
}
