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

    /// <summary>
    /// 所有有效的上下文名列表。
    /// </summary>
    public static readonly string[] AllContexts =
    [
        ContextGlobal, ContextChat, ContextAutocomplete,
    ];

    /// <summary>
    /// 上下文描述映射。
    /// </summary>
    public static readonly Dictionary<string, string> ContextDescriptions = new()
    {
        [ContextGlobal] = "Active everywhere, regardless of focus",
        [ContextChat] = "When the chat input is focused",
        [ContextAutocomplete] = "When autocomplete menu is visible",
    };

    #endregion

    #region 动作常量

    // App 级别动作
    public const string ActionAppExit = "app:exit";
    public const string ActionAppCommandPalette = "app:commandPalette";

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

    // Plan 侧边栏开关（有活动计划时可收起/展开）
    public const string ActionChatTogglePlanPanel = "chat:togglePlanPanel";

    // TEAM 模式专用：在 Magentic ↔ GroupChat 之间切换协作策略
    public const string ActionChatToggleStrategy = "chat:toggleStrategy";

    // TEAM 模式专用：循环切换已注册团队（feature-impl → code-review → research → ...）
    public const string ActionChatCycleTeam = "chat:cycleTeam";

    // Autocomplete 菜单动作
    public const string ActionAutocompletePrevious = "autocomplete:previous";
    public const string ActionAutocompleteNext = "autocomplete:next";

    /// <summary>
    /// 所有有效的标准动作名列表。
    /// </summary>
    public static readonly string[] AllActions =
    [
        ActionAppExit, ActionAppCommandPalette,
        ActionHistoryPrevious, ActionHistoryNext, ActionHistoryRecallLast,
        ActionChatCancel, ActionChatKillAgents,
        ActionChatSubmit, ActionChatNewline, ActionChatPaste,
        ActionChatScrollUp, ActionChatScrollDown,
        ActionChatPageUp, ActionChatPageDown,
        ActionChatTogglePlanPanel,
        ActionChatToggleStrategy, ActionChatCycleTeam,
        ActionAutocompletePrevious, ActionAutocompleteNext,
    ];

    private static readonly HashSet<string> AllActionsSet = new(AllActions);
    private static readonly HashSet<string> AllContextsSet = new(AllContexts);

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

            // 对话区键盘滚动（不干扰输入）
            ["shift+up"] = ActionChatScrollUp,
            ["shift+down"] = ActionChatScrollDown,
            ["ctrl+pgup"] = ActionChatScrollUp,
            ["ctrl+pgdn"] = ActionChatScrollDown,
            ["pageup"] = ActionChatPageUp,
            ["pagedown"] = ActionChatPageDown,

            // Plan 侧边栏开关
            ["ctrl+g"] = ActionChatTogglePlanPanel,

            // 在 TEAM 模式下切换 Magentic ↔ GroupChat 策略
            ["shift+tab"] = ActionChatToggleStrategy,

            // 在 TEAM 模式下循环切换已注册团队（Ctrl+Shift+T）
            ["ctrl+shift+t"] = ActionChatCycleTeam,
        }),
        new(ContextAutocomplete, new Dictionary<string, string?>
        {
            ["up"] = ActionAutocompletePrevious,
            ["down"] = ActionAutocompleteNext,
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
    /// 终端保留快捷键（被终端/OS 拦截）。
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
        reserved.AddRange(TerminalReserved);

        if (OperatingSystem.IsMacOS())
        {
            reserved.AddRange(MacOSReserved);
        }

        return reserved;
    }

    #endregion
}
