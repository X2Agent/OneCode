using System.Collections.ObjectModel;
using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// 快捷键查看 overlay —— 显示当前生效绑定（默认 + 用户自定义合并后），
/// 按 Context 分组并标记自定义项，附硬编码按键与配置校验警告。
/// 由 <c>/keybindings list</c> 触发；Esc 关闭。
/// </summary>
public sealed class KeybindingsOverlay : CenteredOverlay
{
    private const string CustomMark = "★自定义";
    private const string UnboundAction = "(已解绑)";

    private readonly ListView _listView;

    protected override View? InitialFocusView => _listView;

    public KeybindingsOverlay(
        IReadOnlyList<KeybindingView> bindings,
        IReadOnlyList<KeybindingWarning> warnings)
        : base($"  快捷键  ({TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} 滚动 · Esc 关闭)  ", preferredWidth: 80)
    {
        PreferredWidth = 80;

        var rows = FormatRows(bindings, warnings);
        _listView = new ListView
        {
            X = TuiSpacing.OverlayContentX,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill() - 4,
            Height = Dim.Fill() - 2,
            CanFocus = true,
        };
        _listView.SetScheme(TuiTheme.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _listView.SetSource(new ObservableCollection<string>(rows));

        // 高度自适应：行数 + 标题/边框/底边距，clamp 到 60（OverlayHost 还会按视口二次收缩）。
        PreferredHeight = Math.Clamp(rows.Count + 6, 12, 60);

        Add(_listView);
    }

    /// <summary>
    /// 将生效绑定与警告格式化为 overlay 显示行。
    /// internal static 便于单元测试断言真实产出。
    /// </summary>
    internal static List<string> FormatRows(
        IReadOnlyList<KeybindingView> bindings,
        IReadOnlyList<KeybindingWarning> warnings)
    {
        var rows = new List<string>();

        foreach (var group in bindings.GroupBy(b => b.Context, StringComparer.Ordinal))
        {
            rows.Add($"— {group.Key} —");
            foreach (var view in group)
            {
                var action = view.Source == KeybindingSource.Unbound
                    ? UnboundAction
                    : view.Action ?? string.Empty;
                var mark = view.Source == KeybindingSource.Custom ? $"  {CustomMark}" : string.Empty;
                rows.Add($"  {view.KeyDisplay,-18} {action}{mark}");
            }
            rows.Add(string.Empty);
        }

        rows.Add("— 硬编码（不可通过 keybindings.json 重映射）—");
        rows.Add("  Tab                补全确认 / 模式切换");
        rows.Add("  Ctrl+Left/Right    占位符建议切换");
        rows.Add("  /find <keyword>    搜索对话记录");
        rows.Add("  /diff              Git 变更审查 overlay");
        rows.Add(string.Empty);

        if (warnings.Count > 0)
        {
            rows.Add($"— 配置警告 ({warnings.Count}) —");
            foreach (var warning in warnings)
            {
                var icon = warning.Severity == KeybindingSeverity.Error ? "✗" : "⚠";
                rows.Add($"  {icon} {warning.Message}");
            }
            rows.Add(string.Empty);
        }

        rows.Add("/keybindings open 编辑自定义绑定 · Esc 关闭");
        return rows;
    }
}
