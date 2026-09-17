namespace OneCode.Core.Keybindings;

/// <summary>
/// 默认快捷键绑定定义，包含上下文常量、动作常量、默认绑定映射和保留快捷键列表。
/// </summary>
public static class KeybindingDefaults
{
    #region 上下文常量

    public const string ContextGlobal = "Global";
    public const string ContextChat = "Chat";
    public const string ContextAutocomplete = "Autocomplete";

    /// <summary>Diff 审查视图聚焦时（DiffDetailOverlay 内部解析时并入）。</summary>
    public const string ContextDiff = "Diff";

    /// <summary>内联选择器（权限提示 / Plan 审批）接管键盘时并入。</summary>
    public const string ContextSelector = "Selector";

    /// <summary>
    /// 所有有效的上下文名列表。
    /// </summary>
    public static readonly string[] AllContexts =
    [
        ContextGlobal, ContextChat, ContextAutocomplete,
        ContextDiff, ContextSelector,
    ];

    /// <summary>
    /// 上下文描述映射。
    /// </summary>
    public static readonly Dictionary<string, string> ContextDescriptions = new()
    {
        [ContextGlobal] = "Active everywhere, regardless of focus",
        [ContextChat] = "When the chat input is focused",
        [ContextAutocomplete] = "When autocomplete menu is visible",
        [ContextDiff] = "While the diff review view has focus",
        [ContextSelector] = "While an inline selector owns the keyboard",
    };

    #endregion

    #region 动作常量

    // App 级别动作
    public const string ActionAppExit = "app:exit";
    public const string ActionAppSidebarToggle = "app:sidebarToggle";

    // 右侧侧边栏（Plan/TEAM）宽度键盘调整——与分隔线鼠标拖拽等价的键盘路径。
    // 注册于 Global 上下文：输入框聚焦时经 ChatInputView 转发，非聚焦时经
    // ReplShell.OnKeyDown 兜底，两条分发路径共用同一动作。
    public const string ActionAppSidebarWider = "app:sidebarWider";
    public const string ActionAppSidebarNarrower = "app:sidebarNarrower";

    // 工作模式直达（Alt+1..4 独立单键绑定，与裸 Tab 循环解耦——共享 tab 前缀
    // 会与循环/补全语义冲突；经典终端把 ctrl+2/3/4 编码为 NUL/ESC/FS。注册于 Chat 上下文。
    public const string ActionAppModeBuild = "app:modeBuild";
    public const string ActionAppModePlan = "app:modePlan";
    public const string ActionAppModeTeam = "app:modeTeam";
    public const string ActionAppModeGoal = "app:modeGoal";

    // 历史导航
    public const string ActionHistoryPrevious = "history:previous";
    public const string ActionHistoryNext = "history:next";
    public const string ActionHistoryRecallLast = "history:recallLast";

    // Chat 输入动作
    public const string ActionChatCancel = "chat:cancel";
    public const string ActionChatKillAgents = "chat:killAgents";
    public const string ActionChatSubmit = "chat:submit";
    public const string ActionChatNewline = "chat:newline";
    public const string ActionChatPaste = "chat:paste";
    public const string ActionChatScrollUp = "chat:scrollUp";
    public const string ActionChatScrollDown = "chat:scrollDown";
    public const string ActionChatPageUp = "chat:pageUp";
    public const string ActionChatPageDown = "chat:pageDown";

    // TEAM 模式专用：循环切换已注册团队（code-review → research → ...）
    // 绑定 Shift+Tab（Tab 换模式、Shift+Tab 换模式内的团队）。
    public const string ActionChatCycleTeam = "chat:cycleTeam";

    // Autocomplete 菜单动作
    public const string ActionAutocompletePrevious = "autocomplete:previous";
    public const string ActionAutocompleteNext = "autocomplete:next";
    // Tab 接受建议：tab 自身在 Autocomplete 上下文绑定，补全激活时立即触发；
    // 模式直达已改绑 alt+1..4，tab 不再承担和弦前缀职责。
    public const string ActionAutocompleteAccept = "autocomplete:accept";
    public const string ActionAutocompleteDismiss = "autocomplete:dismiss";

    // Diff 审查视图滚动（DiffView 聚焦时并入 ContextDiff 解析）
    public const string ActionDiffScrollUp = "diff:scrollUp";
    public const string ActionDiffScrollDown = "diff:scrollDown";
    public const string ActionDiffPageUp = "diff:pageUp";
    public const string ActionDiffPageDown = "diff:pageDown";
    public const string ActionDiffTop = "diff:top";
    public const string ActionDiffBottom = "diff:bottom";

    // 内联选择器（权限提示 / Plan 审批）导航与确认
    public const string ActionSelectorPrevious = "selector:previous";
    public const string ActionSelectorNext = "selector:next";
    public const string ActionSelectorConfirm = "selector:confirm";
    public const string ActionSelectorDismiss = "selector:dismiss";

    /// <summary>
    /// 所有有效的标准动作名列表。
    /// </summary>
    public static readonly string[] AllActions =
    [
        ActionAppExit,
        ActionAppSidebarToggle,
        ActionAppSidebarWider, ActionAppSidebarNarrower,
        ActionAppModeBuild, ActionAppModePlan, ActionAppModeTeam, ActionAppModeGoal,
        ActionHistoryPrevious, ActionHistoryNext, ActionHistoryRecallLast,
        ActionChatCancel, ActionChatKillAgents,
        ActionChatSubmit, ActionChatNewline, ActionChatPaste,
        ActionChatScrollUp, ActionChatScrollDown,
        ActionChatPageUp, ActionChatPageDown,
        ActionChatCycleTeam,
        ActionAutocompletePrevious, ActionAutocompleteNext,
        ActionAutocompleteAccept, ActionAutocompleteDismiss,
        ActionDiffScrollUp, ActionDiffScrollDown,
        ActionDiffPageUp, ActionDiffPageDown, ActionDiffTop, ActionDiffBottom,
        ActionSelectorPrevious, ActionSelectorNext,
        ActionSelectorConfirm, ActionSelectorDismiss,
    ];

    private static readonly HashSet<string> AllActionsSet = new(AllActions);
    private static readonly HashSet<string> AllContextsSet = new(AllContexts);

    /// <summary>
    /// 动作 → 人类可读功能说明（中文），供 /keybindings list 与 TUI overlay 显示。
    /// 必须覆盖 <see cref="AllActions"/> 中的每个动作（测试守护：AllActionDescriptions_CoversEveryAction）。
    /// </summary>
    public static readonly Dictionary<string, string> AllActionDescriptions = new()
    {
        // App 级别
        [ActionAppExit] = "退出应用",
        [ActionAppSidebarToggle] = "切换右侧侧边栏可见性（Plan/TEAM 面板）",
        [ActionAppSidebarWider] = "加宽右侧侧边栏（Plan/TEAM 面板）",
        [ActionAppSidebarNarrower] = "收窄右侧侧边栏（Plan/TEAM 面板）",
        [ActionAppModeBuild] = "切换到 BUILD 模式",
        [ActionAppModePlan] = "切换到 PLAN 模式",
        [ActionAppModeTeam] = "切换到 TEAM 模式",
        [ActionAppModeGoal] = "切换到 GOAL 模式",

        // 历史导航
        [ActionHistoryPrevious] = "上一条历史输入",
        [ActionHistoryNext] = "下一条历史输入",
        [ActionHistoryRecallLast] = "召回上一条用户消息以便编辑重发",

        // Chat 输入
        [ActionChatCancel] = "中断模型响应（空闲时关闭补全）",
        [ActionChatKillAgents] = "中断运行中的查询（可自定义和弦）",
        [ActionChatSubmit] = "提交消息",
        [ActionChatNewline] = "输入换行",
        [ActionChatPaste] = "智能粘贴（图片/路径/大文本折叠）",
        [ActionChatScrollUp] = "对话区向上滚动（行级）",
        [ActionChatScrollDown] = "对话区向下滚动（行级）",
        [ActionChatPageUp] = "对话区向上翻页",
        [ActionChatPageDown] = "对话区向下翻页",
        [ActionChatCycleTeam] = "TEAM 模式下循环切换已注册团队",

        // Autocomplete
        [ActionAutocompletePrevious] = "上一条补全建议",
        [ActionAutocompleteNext] = "下一条补全建议",
        [ActionAutocompleteAccept] = "接受当前补全",
        [ActionAutocompleteDismiss] = "关闭补全菜单",

        // Diff 审查
        [ActionDiffScrollUp] = "向上滚动一行",
        [ActionDiffScrollDown] = "向下滚动一行",
        [ActionDiffPageUp] = "向上翻页滚动",
        [ActionDiffPageDown] = "向下翻页滚动",
        [ActionDiffTop] = "跳到顶部",
        [ActionDiffBottom] = "跳到底部",

        // 内联选择器
        [ActionSelectorPrevious] = "上一选项",
        [ActionSelectorNext] = "下一选项",
        [ActionSelectorConfirm] = "确认当前选项",
        [ActionSelectorDismiss] = "取消选择器",
    };

    /// <summary>
    /// 检查是否是有效的上下文名。
    /// </summary>
    public static bool IsValidContext(string context) => AllContextsSet.Contains(context);

    /// <summary>
    /// 检查是否是有效的动作名（标准动作或 command: 前缀）。
    /// </summary>
    public static bool IsValidAction(string action) =>
        AllActionsSet.Contains(action) || action.StartsWith("command:", StringComparison.Ordinal);

    #endregion

    #region 默认绑定

    /// <summary>
    /// 默认快捷键绑定映射。
    /// </summary>
    public static readonly KeybindingBlock[] DefaultBindings =
    [
        new(ContextGlobal, new Dictionary<string, string?>
        {
            ["ctrl+d"] = ActionAppExit,
            ["ctrl+g"] = ActionAppSidebarToggle,

            // 侧边栏宽度调整（Plan/TEAM 面板）：Ctrl+Shift+方向键，步进见
            // SidebarViewBase.KeyboardResizeStep。不占用 ctrl+left/right（占位建议循环）。
            ["ctrl+shift+right"] = ActionAppSidebarWider,
            ["ctrl+shift+left"] = ActionAppSidebarNarrower,
        }),
        new(ContextChat, new Dictionary<string, string?>
        {
            ["escape"] = ActionChatCancel,
            ["enter"] = ActionChatSubmit,
            ["up"] = ActionHistoryPrevious,
            ["down"] = ActionHistoryNext,
            ["ctrl+up"] = ActionHistoryRecallLast,
            ["shift+enter"] = ActionChatNewline,
            ["alt+enter"] = ActionChatNewline,
            ["ctrl+v"] = ActionChatPaste,

            // 对话区键盘滚动（不干扰输入）。
            // ctrl+d 不在此绑定——它是 Global 保留退出键（app:exit），
            // 且 Chat 块声明在 Global 之后，「后匹配生效」会永久遮蔽退出键。
            ["shift+up"] = ActionChatScrollUp,
            ["shift+down"] = ActionChatScrollDown,
            ["ctrl+pgup"] = ActionChatScrollUp,
            ["ctrl+pgdn"] = ActionChatScrollDown,
            ["pageup"] = ActionChatPageUp,
            ["pagedown"] = ActionChatPageDown,

            // TEAM 模式下循环切换已注册团队（Shift+Tab）。
            // 编排模式由 team.yaml 固定声明，运行期不可覆盖，无策略切换键。
            ["shift+tab"] = ActionChatCycleTeam,

            // 工作模式直达（Alt+数字独立绑定；裸 Tab 循环为硬编码行为，不经 Resolver，
            // 两者无共享前缀、互不干扰）。
            ["alt+1"] = ActionAppModeBuild,
            ["alt+2"] = ActionAppModePlan,
            ["alt+3"] = ActionAppModeTeam,
            ["alt+4"] = ActionAppModeGoal,
        }),
        new(ContextAutocomplete, new Dictionary<string, string?>
        {
            ["up"] = ActionAutocompletePrevious,
            ["down"] = ActionAutocompleteNext,
            // Tab 接受 / Esc 关闭：经 eager-fire 与 Chat 的 tab N 和弦共存
            //（补全未激活时 Autocomplete 上下文不活跃，Tab 仍走模式循环）。
            ["tab"] = ActionAutocompleteAccept,
            ["escape"] = ActionAutocompleteDismiss,
        }),
        // Diff 审查视图滚动。Esc 关闭 overlay 属保留行为（见 docs「保留行为」），
        // 不在此注册，保证关闭永远可用。
        new(ContextDiff, new Dictionary<string, string?>
        {
            ["up"] = ActionDiffScrollUp,
            ["k"] = ActionDiffScrollUp,
            ["down"] = ActionDiffScrollDown,
            ["j"] = ActionDiffScrollDown,
            ["pageup"] = ActionDiffPageUp,
            ["pagedown"] = ActionDiffPageDown,
            ["home"] = ActionDiffTop,
            ["end"] = ActionDiffBottom,
        }),
        new(ContextSelector, new Dictionary<string, string?>
        {
            ["up"] = ActionSelectorPrevious,
            ["down"] = ActionSelectorNext,
            ["enter"] = ActionSelectorConfirm,
            ["escape"] = ActionSelectorDismiss,
        }),
    ];

    /// <summary>
    /// 获取默认解析后的绑定条目列表。
    /// </summary>
    public static List<KeybindingEntry> GetDefaultParsedBindings() =>
        KeybindingParser.ParseBindings(DefaultBindings);

    #endregion

    #region 保留快捷键

    /// <summary>
    /// 不可重新绑定的快捷键（硬编码行为）。
    /// </summary>
    public static readonly ReservedShortcut[] NonRebindable =
    [
        new("ctrl+d", "Cannot be rebound - used for exit (hardcoded)", KeybindingSeverity.Error),
        new("ctrl+m", "Cannot be rebound - identical to Enter in terminals (both send CR)", KeybindingSeverity.Error),
    ];

    /// <summary>
    /// 终端保留快捷键（被终端/OS 拦截）。仅适用于 Unix 终端信号；
    /// Windows 控制台不发送 SIGTSTP/SIGQUIT，这些键可正常绑定。
    /// </summary>
    public static readonly ReservedShortcut[] TerminalReserved =
    [
        new("ctrl+z", "Unix process suspend (SIGTSTP)", KeybindingSeverity.Warning),
        new("ctrl+\\", "Terminal quit signal (SIGQUIT)", KeybindingSeverity.Error),
    ];

    /// <summary>
    /// macOS 保留快捷键（被 OS 拦截）。
    /// </summary>
    public static readonly ReservedShortcut[] MacOSReserved =
    [
        new("cmd+c", "macOS system copy", KeybindingSeverity.Error),
        new("cmd+v", "macOS system paste", KeybindingSeverity.Error),
        new("cmd+x", "macOS system cut", KeybindingSeverity.Error),
        new("cmd+q", "macOS quit application", KeybindingSeverity.Error),
        new("cmd+w", "macOS close window/tab", KeybindingSeverity.Error),
        new("cmd+tab", "macOS app switcher", KeybindingSeverity.Error),
        new("cmd+space", "macOS Spotlight", KeybindingSeverity.Error),
    ];

    /// <summary>
    /// 获取当前平台的保留快捷键列表。
    /// </summary>
    public static List<ReservedShortcut> GetReservedShortcuts()
    {
        var reserved = new List<ReservedShortcut>();
        reserved.AddRange(NonRebindable);

        if (!OperatingSystem.IsWindows())
        {
            reserved.AddRange(TerminalReserved);
        }

        if (OperatingSystem.IsMacOS())
        {
            reserved.AddRange(MacOSReserved);
        }

        return reserved;
    }

    #endregion
}
