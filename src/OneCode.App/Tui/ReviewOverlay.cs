using System.Collections.ObjectModel;

namespace OneCode.App.Tui;

/// <summary>
/// Review overlay — design-spec §3. Triggered by <c>/diff</c> (no args).
/// File list with diff stats + hunk navigation.
/// ↑/↓ select file, Enter open, Esc returns.
///
/// Diff data is supplied by the caller (typically <see cref="IGitHelper"/>).
/// </summary>
public sealed class ReviewOverlay : CenteredOverlay
{
    private readonly List<ReviewFileEntry> _files;
    private readonly ListView _listView;

    public event Action<ReviewFileEntry>? FileSelected;

    protected override View? InitialFocusView => _listView;

    public ReviewOverlay(IReadOnlyList<ReviewFileEntry>? files = null)
        : base($"  代码审查  ({TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} 选择 · Enter 查看 · Esc 关闭)  ", preferredWidth: 80, preferredHeight: 18)
    {
        // Width scales via OverlayHost (up to 70% of terminal); height fits the file list.
        // Soft ceiling of 60 rows — host clamps further to 75% of viewport on resize.
        PreferredWidth = 80;
        _files = (files ?? new List<ReviewFileEntry>()).ToList();
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
        header.SetScheme(TuiTheme.MakeScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        _listView = new ListView
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY + 2,
            Width = Dim.Fill() - 4,
            Height = Dim.Fill() - 3,
            CanFocus = true,
        };
        _listView.SetScheme(TuiTheme.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _listView.SetSource(new ObservableCollection<string>(FormatEntries()));

        // Default select first item
        if (_files.Count > 0)
            _listView.SelectedItem = 0;

        _listView.KeyDown += (_, key) =>
        {
            if (key == Key.Enter && _files.Count > 0)
            {
                InvokeFileSelected();
                key.Handled = true;
            }
        };

        Add(header, _listView);
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
