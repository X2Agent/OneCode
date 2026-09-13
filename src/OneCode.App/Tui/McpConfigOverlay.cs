using OneCode.Core.Mcp;
using System.Collections.ObjectModel;

namespace OneCode.App.Tui;

/// <summary>进入配置页时单个 MCP 服务器的快照（配置 + 运行时方法列表）。</summary>
public sealed record McpConfigServerEntry(
    string Name,
    bool Disabled,
    bool Connected,
    IReadOnlyList<string>? EnabledTools,
    IReadOnlyList<string> LiveTools)
{
    public string ListText =>
        $"{(Disabled ? TuiGlyphs.Pending : TuiGlyphs.Complete)} {Name,-22} {(Disabled ? "[已禁用]" : Connected ? $"[已连接 {LiveTools.Count} 方法]" : "[未连接]")}";
}

/// <summary>配置页保存结果：每个被修改服务器的最终勾选状态。</summary>
public sealed record McpConfigResult(IReadOnlyList<McpConfigServerChange> Servers);

/// <summary>单个服务器的保存结果。EnabledTools 为 null 表示保持"全部暴露"。</summary>
public sealed record McpConfigServerChange(
    string Name,
    bool? Disabled,
    IReadOnlyList<string>? EnabledTools);

/// <summary>
/// MCP 工具白名单配置页。左侧服务器列表（含启用开关），右侧方法级勾选。
/// 保存时通过 <see cref="McpConfigResult"/> 交回调用方写配置 + 热生效，
/// overlay 本身不做 I/O（与 SettingsOverlay 的关注点分离一致）。
/// </summary>
public sealed class McpConfigOverlay : FormOverlay<McpConfigResult?>
{
    private static readonly Scheme CheckScheme = new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgCard),
        Focus = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgCard),
    };

    private sealed record ServerEditState(
        McpConfigServerEntry Entry,
        bool Disabled,
        IReadOnlyList<string>? InitialWhitelist,
        List<string> Checked,
        bool WhitelistTouched);

    private readonly List<McpConfigServerEntry> _entries;
    private readonly Dictionary<string, ServerEditState> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<string> _serverItems = [];
    private readonly ObservableCollection<string> _toolItems = [];
    private readonly ListView _serverList;
    private readonly ListView _toolList = null!; // 构造函数内赋值；KeyDown lambda 中引用需非空声明
    private readonly CheckBox _serverEnabledCheck;
    private readonly Label _statusLabel;
    private readonly Label _hintLabel;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private int _selectedIndex = -1;

    protected override View? InitialFocusView => _serverList;

    internal ListView ServerList => _serverList;
    internal ListView ToolList => _toolList;
    internal CheckBox ServerEnabledCheck => _serverEnabledCheck;

    public McpConfigOverlay(IReadOnlyList<McpConfigServerEntry> entries)
        : base("MCP 工具白名单", preferredWidth: 84, preferredHeight: 24)
    {
        _entries = [.. entries];
        foreach (var entry in _entries)
        {
            _edits[entry.Name] = new ServerEditState(
                entry, entry.Disabled, entry.EnabledTools,
                Checked: entry.EnabledTools is null
                    ? [.. entry.LiveTools]
                    : [.. entry.LiveTools.Where(t => McpToolFilter.IsEnabled(entry.EnabledTools, t))],
                WhitelistTouched: false);
            _serverItems.Add(entry.ListText);
        }

        _hintLabel = new Label
        {
            // 单行提示：空格两栏一致；←→ 在两栏间方向性切换；Tab/Shift+Tab 走
            // 框架 TabStop 链（服务器列表 → 方法列表 → 启用复选框 → 保存/取消），
            // 是焦点离开两个列表的唯一键盘出口，与提示一并说明。
            Text = "←→ 切换面板 · Tab 遍历控件 · 空格 勾选/取消",
            X = TuiSpacing.OverlayContentX,
            Y = 0,
            Width = Dim.Fill(TuiSpacing.OverlayContentX + TuiSpacing.Md),
            CanFocus = false,
        };
        _hintLabel.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        var serverFrame = new FrameView
        {
            X = TuiSpacing.OverlayContentX,
            Y = 0,
            Width = TuiSpacing.McpServerPaneWidth, // 窄对话框下由 Dim.Func 动态收缩
            Height = 13,
            Title = " 服务器 ",
        };
        // 服务器窗格常规宽 36；对话框变窄时让位给方法窗格，但自身保底不折叠。
        serverFrame.Width = Dim.Func(_ =>
        {
            var available = ResolveHostWidth(serverFrame)
                - 2 * TuiSpacing.OverlayContentX - TuiSpacing.McpPaneGap - TuiSpacing.McpToolPaneMinWidth;
            return Math.Clamp(
                TuiSpacing.McpServerPaneWidth,
                TuiSpacing.McpServerPaneMinWidth,
                Math.Max(TuiSpacing.McpServerPaneMinWidth, available));
        }, serverFrame);
        _serverList = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true,
        };
        _serverList.SetSource(_serverItems);
        _serverList.SetScheme(TuiTheme.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _serverList.ValueChanged += (_, _) =>
        {
            var index = _serverList.SelectedItem;
            if (index is int i && i >= 0 && i < _entries.Count && i != _selectedIndex)
                SelectServer(i);
        };
        _serverList.KeyDown += (_, key) =>
        {
            if (HandleListKey(_serverList, key))
                key.Handled = true;
        };
        serverFrame.Add(_serverList);

        _serverEnabledCheck = new CheckBox
        {
            Text = "启用此服务器",
            X = TuiSpacing.OverlayContentX,
            Y = 13,
        };
        _serverEnabledCheck.SetScheme(CheckScheme);
        _serverEnabledCheck.ValueChanged += (_, _) => ToggleServerEnabled();

        var toolFrame = new FrameView
        {
            X = Pos.Right(serverFrame) + TuiSpacing.McpPaneGap,
            Y = 0,
            Height = 13,
            Title = " 方法 ",
        };
        // Terminal.Gui v2 的 Dim.Fill(margin) 是 X 感知的：宽 = 父宽 − 自身X − margin。
        // margin 只取右侧对称留白 OverlayContentX；配合服务器窗格 Dim.Func 收缩
        //（S ≤ 父宽 − 2*OverlayContentX − McpPaneGap − McpToolPaneMinWidth），
        // 方法窗格宽 = 父宽 − (3+S+2) − 3 ≥ McpToolPaneMinWidth，窄对话框下永不塌缩。
        // 但如果父宽来自带有更大 ContentSize 的可滚动容器，父宽需基于当前 Viewport 或 Overlay 实际宽度。
        toolFrame.Width = Dim.Func(_ =>
        {
            var serverRight = serverFrame.Frame.Right > 0 ? serverFrame.Frame.Right : (TuiSpacing.OverlayContentX + TuiSpacing.McpServerPaneMinWidth);
            var w = ResolveHostWidth(serverFrame) - (serverRight + TuiSpacing.McpPaneGap) - TuiSpacing.OverlayContentX;
            return Math.Max(TuiSpacing.McpToolPaneMinWidth, w);
        }, toolFrame);
        _toolList = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true,
        };
        _toolList.SetSource(_toolItems);
        _toolList.SetScheme(TuiTheme.MakeListScheme(TuiPalette.FgPrimary, TuiPalette.BgCard));
        _toolList.KeyDown += (_, key) =>
        {
            if (HandleListKey(_toolList, key))
                key.Handled = true;
        };
        toolFrame.Add(_toolList);

        _statusLabel = new Label
        {
            Text = string.Empty,
            X = TuiSpacing.OverlayContentX,
            Y = 0,
            Width = Dim.Fill(TuiSpacing.OverlayContentX + TuiSpacing.Md),
            Height = 1,
            CanFocus = false,
        };
        _statusLabel.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        // 经 AddCustomRow 推进行游标，AddActionBar 才能落在内容底部（而非顶部）。
        AddCustomRow(1, _hintLabel);
        AddCustomRow(14, serverFrame, toolFrame, _serverEnabledCheck);
        AddCustomRow(1, _statusLabel);
        (_saveButton, _cancelButton) = AddActionBar(
            $"{TuiGlyphs.Complete} _保存  Ctrl+S",
            TrySave,
            "_取消  Esc",
            () => RequestClose(OverlayCloseReason.Cancelled));

        if (_entries.Count > 0)
            SelectServer(0);
    }

    /// <summary>
    /// 解析双栏布局的可用父宽度：优先取直接父容器宽度；父容器尚未测量（宽 ≤ 0）时
    /// 回退到祖父（Overlay）宽度。Dim.Func 的求值可能早于父容器布局，故需此回退。
    /// 集中一处，避免服务器/方法两个窗格各自维护同一段兜底逻辑而漂移。
    /// </summary>
    private static int ResolveHostWidth(View anchor)
    {
        var hostWidth = (int?)anchor.SuperView?.Frame.Width ?? TuiSpacing.OverlayDefaultWidth;
        if (hostWidth <= 0 && anchor.SuperView?.SuperView is { } grandparent && grandparent.Frame.Width > 0)
            hostWidth = grandparent.Frame.Width;
        return hostWidth;
    }

    protected override McpConfigResult? GetDismissedResult(OverlayCloseReason reason) => null;

    private void SelectServer(int index)
    {
        _selectedIndex = index;
        var state = _edits[_entries[index].Name];
        _serverEnabledCheck.Value = state.Disabled ? CheckState.UnChecked : CheckState.Checked;
        RefreshToolList();
    }

    /// <summary>顶部提示文案（internal 供 headless 测试断言）。</summary>
    internal string HintText => _hintLabel.Text;

    /// <summary>
    /// 两个列表共用的按键处理：空格/Enter 切换勾选；→ 仅在服务器列表中
    /// 前往方法面板，← 仅在方法列表中返回服务器面板（方向与布局一致）。
    /// Tab/Shift+Tab 不在此消费，由 overlay 级焦点环统一遍历全部控件
    /// （见 <see cref="CycleTabStop"/>）。返回 true 表示已消费该键。
    /// </summary>
    /// <param name="source">触发事件的列表（决定勾选目标与切换方向）。</param>
    private bool HandleListKey(ListView source, Key key)
    {
        if (key == Key.Space || key == Key.Enter)
        {
            if (source == _serverList)
                ToggleServerEnabled();
            else
                ToggleTool();
            return true;
        }

        if (key == Key.CursorRight && source == _serverList)
        {
            SwitchPane(toolPane: true);
            return true;
        }
        if (key == Key.CursorLeft && source == _toolList)
        {
            SwitchPane(toolPane: false);
            return true;
        }

        // 反向方向键（服务器列表按 ← / 方法列表按 →）不消费：
        // 框架将其别名为 Previous/NextTabStop，自然沿 Tab 链前进/后退。
        return false;
    }

    /// <summary>
    /// 在服务器/方法两个面板间切换焦点（internal 供 headless 测试驱动）。
    /// </summary>
    /// <remarks>
    /// Terminal.Gui 的 <see cref="View.SetFocus"/> 在真实 App 下依赖
    /// Application.Navigation 维护的焦点缓存，可能被静默否决（headless 无
    /// Navigation 走无条件路径，无法复现）。目标列表不可聚焦时直接放弃切换，
    /// 绝不退而聚焦宿主 FrameView：容器 SetFocus 只保证自身 _hasFocus，
    /// RestoreFocus/AdvanceFocus 下沉失败时焦点会滞留在标题框，而 FrameView
    /// 构造器已 KeyBindings.Clear()，滞留的 ←/→ 会冒泡到 shell 层失控。
    /// </remarks>
    internal void SwitchPane(bool toolPane)
    {
        ListView target = toolPane ? _toolList : _serverList;
        if (target.SetFocus() || target.HasFocus)
            return;
    }

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.S.WithCtrl)
        {
            TrySave();
            return true;
        }

        // Tab/Shift+Tab 沿显式焦点环遍历（服务器列表 → 方法列表 → 启用复选框 →
        // 保存 → 取消，末尾回绕）。不能依赖框架 TabStop 链：两个窗格 FrameView
        // 与操作栏容器都是 TabBehavior.TabGroup，会被 TabStop 导航过滤掉，
        // 焦点会越过列表直达复选框。焦点环让"Tab 跳出两个列表"始终成立。
        if (kb == Key.Tab)
        {
            return CycleTabStop(forward: true);
        }
        if (kb == Key.Tab.WithShift)
        {
            return CycleTabStop(forward: false);
        }

        return base.OnKeyDown(kb);
    }

    /// <summary>Tab 遍历顺序：两个列表 → 启用复选框 → 保存 → 取消（回绕）。</summary>
    private View[] TabStops => [_serverList, _toolList, _serverEnabledCheck, _saveButton, _cancelButton];

    /// <summary>沿 Tab 遍历顺序前进/后退一步（基于最深焦点视图定位当前站）。</summary>
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

    /// <summary>启用/禁用当前服务器（internal 供 headless 测试驱动）。</summary>
    internal void ToggleServerEnabled()
    {
        if (_selectedIndex < 0) return;
        var state = _edits[_entries[_selectedIndex].Name];
        var wantDisabled = _serverEnabledCheck.Value == CheckState.UnChecked;
        if (state.Disabled == wantDisabled)
            return; // checkbox 回写触发的事件，状态已一致（防止键盘切换 → 事件 → 再切换的死循环）

        var updated = state with { Disabled = wantDisabled };
        _edits[state.Entry.Name] = updated;
        _serverEnabledCheck.Value = wantDisabled ? CheckState.UnChecked : CheckState.Checked;
        var listEntry = updated.Entry with { Disabled = wantDisabled };
        _serverItems[_selectedIndex] = listEntry.ListText;
        RefreshToolList();
    }

    /// <summary>切换当前服务器、高亮方法的勾选状态（internal 供 headless 测试驱动）。</summary>
    internal void ToggleTool()
    {
        if (_selectedIndex < 0) return;
        var state = _edits[_entries[_selectedIndex].Name];
        var index = _toolList.SelectedItem ?? -1;
        if (index < 0 || index >= state.Entry.LiveTools.Count) return;

        var toolName = state.Entry.LiveTools[index];
        var checkedSet = new HashSet<string>(state.Checked, StringComparer.OrdinalIgnoreCase);
        if (!checkedSet.Remove(toolName))
            checkedSet.Add(toolName);

        _edits[state.Entry.Name] = state with
        {
            Checked = [.. state.Entry.LiveTools.Where(checkedSet.Contains)],
            WhitelistTouched = true,
        };
        RefreshToolList();
    }

    private void RefreshToolList()
    {
        _toolItems.Clear();
        if (_selectedIndex < 0) return;
        var state = _edits[_entries[_selectedIndex].Name];

        if (state.Entry.LiveTools.Count == 0)
        {
            _toolItems.Add("（未连接 — 连接后可获取方法列表）");
            _statusLabel.Text = state.InitialWhitelist is null
                ? "白名单：未配置（连接后全部暴露）"
                : $"白名单：{string.Join(", ", state.InitialWhitelist)}";
            return;
        }

        foreach (var tool in state.Entry.LiveTools)
            _toolItems.Add($"{(state.Checked.Contains(tool) ? TuiGlyphs.Complete : TuiGlyphs.Pending)} {tool}");

        _statusLabel.Text = !state.WhitelistTouched && state.InitialWhitelist is null
            ? $"白名单：未配置（全部 {state.Entry.LiveTools.Count} 个方法暴露）"
            : $"白名单：{state.Checked.Count}/{state.Entry.LiveTools.Count} 个方法将暴露";
    }

    /// <summary>保存并完成 overlay（internal 供 headless 测试驱动保存路径）。</summary>
    internal void TrySave()
    {
        var changes = new List<McpConfigServerChange>();
        foreach (var entry in _entries)
        {
            var state = _edits[entry.Name];

            // 白名单只在用户实际勾选过或原本就有配置时写回，避免把"没动过"折叠成空名单。
            IReadOnlyList<string>? tools = state.WhitelistTouched || state.InitialWhitelist is not null
                ? state.Checked
                : null;

            bool? disabled = state.Disabled != entry.Disabled ? state.Disabled : null;
            if (tools is null && disabled is null)
                continue;

            changes.Add(new McpConfigServerChange(entry.Name, disabled, tools));
        }

        ShowValidationFailure(null);
        Complete(new McpConfigResult(changes));
    }
}
