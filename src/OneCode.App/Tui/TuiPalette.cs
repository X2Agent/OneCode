namespace OneCode.App.Tui;

/// <summary>
/// 集中管理 TUI 的所有颜色常量，使用 Terminal.Gui v2 的 24-bit TrueColor。
/// 终端不支持 TrueColor 时 Terminal.Gui 自动降级到最接近的 16 色。
///
/// 配色来源：code.pen 设计稿 hex 值。
/// </summary>
internal static class TuiPalette
{
    // 品牌主色
    /// <summary>品牌主色（Accent），Teal #14B8A6。</summary>
    public static readonly Color Accent = new(0x14, 0xB8, 0xA6);

    /// <summary>品牌次色，用于较弱高亮（如 hover/focus 弱态）。#0E8A80。</summary>
    public static readonly Color AccentDim = new(0x0E, 0x8A, 0x80);

    // 语义色
    /// <summary>成功 / 已完成 / 已通过。#22C55E。</summary>
    public static readonly Color Success = new(0x22, 0xC5, 0x5E);

    /// <summary>错误 / 失败 / 已拒绝。#EF4444。</summary>
    public static readonly Color Error = new(0xEF, 0x44, 0x44);

    /// <summary>警告 / 待确认 / 注意。#F59E0B。</summary>
    public static readonly Color Warning = new(0xF5, 0x9E, 0x0B);

    /// <summary>信息 / 进行中 / 普通高亮。#58A6FF。</summary>
    public static readonly Color Info = new(0x58, 0xA6, 0xFF);

    // 状态色（与语义色区分）
    /// <summary>规划/进行中（与 InProgress 符号配合）。</summary>
    public static readonly Color InProgress = new(0x14, 0xB8, 0xA6);

    /// <summary>流式输出（次级文本）。#8B949E。</summary>
    public static readonly Color Streaming = new(0x8B, 0x94, 0x9E);

    /// <summary>思考过程文本。#5BB8C8。</summary>
    public static readonly Color Thinking = new(0x5B, 0xB8, 0xC8);

    // 文本色
    /// <summary>主前景（默认正文）。#E6EDF3。</summary>
    public static readonly Color FgPrimary = new(0xE6, 0xED, 0xF3);

    /// <summary>次前景（描述/说明）。#8B949E。</summary>
    public static readonly Color FgSecondary = new(0x8B, 0x94, 0x9E);

    /// <summary>弱前景（分隔线/弱提示）。#6B7280。</summary>
    public static readonly Color FgMuted = new(0x6B, 0x72, 0x80);

    // 背景色
    /// <summary>主背景。#0A0A0A。</summary>
    public static readonly Color BgPrimary = new(0x0A, 0x0A, 0x0A);

    /// <summary>终端背景。#0D1117。</summary>
    public static readonly Color BgTerminal = new(0x0D, 0x11, 0x17);

    /// <summary>终端头部。#161B22。</summary>
    public static readonly Color BgTerminalHeader = new(0x16, 0x1B, 0x22);

    /// <summary>卡片背景。#111111。</summary>
    public static readonly Color BgCard = new(0x11, 0x11, 0x11);

    /// <summary>错误面板背景。#1A0A0A。</summary>
    public static readonly Color BgError = new(0x1A, 0x0A, 0x0A);

    /// <summary>成功面板背景。#0A160A。</summary>
    public static readonly Color BgSuccess = new(0x0A, 0x16, 0x0A);

    // UI 框架色
    /// <summary>普通边框色。#21262D。</summary>
    public static readonly Color Border = new(0x21, 0x26, 0x2D);

    /// <summary>强调边框色（用于对话框、终端头部）。</summary>
    public static readonly Color BorderAccent = new(0x14, 0xB8, 0xA6);

    /// <summary>分隔线色。</summary>
    public static readonly Color Separator = new(0x21, 0x26, 0x2D);

    // 角色色（消息流）
    /// <summary>用户消息。#58A6FF。</summary>
    public static readonly Color UserMessage = new(0x58, 0xA6, 0xFF);

    /// <summary>助手消息。#E6EDF3。</summary>
    public static readonly Color AssistantMessage = new(0xE6, 0xED, 0xF3);

    /// <summary>工具调用（进行中）。#F59E0B。</summary>
    public static readonly Color ToolUse = new(0xF5, 0x9E, 0x0B);

    /// <summary>工具结果。#D29922。</summary>
    public static readonly Color ToolResult = new(0xD2, 0x99, 0x22);

    /// <summary>系统消息。#BC8CFF。</summary>
    public static readonly Color SystemMessage = new(0xBC, 0x8C, 0xFF);

    // Diff 色
    /// <summary>Diff 新增行。#22C55E。</summary>
    public static readonly Color DiffAdded = new(0x22, 0xC5, 0x5E);

    /// <summary>Diff 删除行。#EF4444。</summary>
    public static readonly Color DiffRemoved = new(0xEF, 0x44, 0x44);

    /// <summary>Diff Hunk header（@@ -12,14 +12,12 @@）。#14B8A6。</summary>
    public static readonly Color DiffHunk = new(0x14, 0xB8, 0xA6);

    /// <summary>Diff 上下文行。#8B949E。</summary>
    public static readonly Color DiffContext = new(0x8B, 0x94, 0x9E);

    // 模式色（工作模式标签）
    /// <summary>Build 模式前景色。#4CAF84。</summary>
    public static readonly Color ModeBuildFg = new(0x4C, 0xAF, 0x84);

    /// <summary>Plan 模式前景色。#5B8DEE。</summary>
    public static readonly Color ModePlanFg = new(0x5B, 0x8D, 0xEE);

    /// <summary>Team 模式前景色。#A386D8。</summary>
    public static readonly Color ModeTeamFg = new(0xA3, 0x86, 0xD8);

    /// <summary>Goal 模式前景色。#5BB8C8。</summary>
    public static readonly Color ModeGoalFg = new(0x5B, 0xB8, 0xC8);

    // Agent 8-色系统（design-spec §6.2）
    /// <summary>orchestrator — 紫色 #A386D8。</summary>
    public static readonly Color AgentPurple = new(0xA3, 0x86, 0xD8);
    /// <summary>researcher — 蓝色 #5B8DEE。</summary>
    public static readonly Color AgentBlue = new(0x5B, 0x8D, 0xEE);
    /// <summary>planner — 绿色 #4CAF84。</summary>
    public static readonly Color AgentGreen = new(0x4C, 0xAF, 0x84);
    /// <summary>executor — 橙色 #E08B5C。</summary>
    public static readonly Color AgentOrange = new(0xE0, 0x8B, 0x5C);
    /// <summary>reviewer — 黄色 #E5B14C。</summary>
    public static readonly Color AgentYellow = new(0xE5, 0xB1, 0x4C);
    /// <summary>tester — 红色 #E0556A。</summary>
    public static readonly Color AgentRed = new(0xE0, 0x55, 0x6A);
    /// <summary>debugger — 粉色 #E07BA5。</summary>
    public static readonly Color AgentPink = new(0xE0, 0x7B, 0xA5);
    /// <summary>assistant — 青色 #5BB8C8。</summary>
    public static readonly Color AgentCyan = new(0x5B, 0xB8, 0xC8);

    /// <summary>活跃背景色。#161B22。</summary>
    public static readonly Color BgActive = new(0x16, 0x1B, 0x22);

    /// <summary>状态正常色。</summary>
    public static readonly Color StatusOk = new(0x22, 0xC5, 0x5E);

    // 消息美化新增色
    /// <summary>工具详情行（args、duration）。#8B949E。</summary>
    public static readonly Color ToolDetailColor = new(0x8B, 0x94, 0x9E);

    /// <summary>Thinking 耗时标识色。#D29922。</summary>
    public static readonly Color ThoughtTimingColor = new(0xD2, 0x99, 0x22);

    /// <summary>根据当前工作模式返回用户消息 ▎ 色（Build=绿, Plan=蓝, Team=紫, Goal=青）。</summary>
    public static Color GetModeBarColor(WorkingMode mode) => mode switch
    {
        WorkingMode.Build => ModeBuildFg,
        WorkingMode.Plan => ModePlanFg,
        WorkingMode.Team => ModeTeamFg,
        WorkingMode.Goal => ModeGoalFg,
        _ => Accent,
    };

    /// <summary>
    /// 根据 agent 名称返回对应的前景色（design-spec §6.2 八色系统）。
    /// 未知名称返回默认 AgentPurple。
    /// </summary>
    public static Color FromAgentName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return AgentPurple;
        return name.Trim().ToLowerInvariant() switch
        {
            "orchestrator" or "coordinator" => AgentPurple,
            "researcher" => AgentBlue,
            "planner" or "plan" => AgentGreen,
            "executor" or "builder" or "build" => AgentOrange,
            "reviewer" => AgentYellow,
            "tester" => AgentRed,
            "debugger" => AgentPink,
            "assistant" or "onecode" => AgentCyan,
            "user" => UserMessage,
            _ => AgentPurple,
        };
    }

    /// <summary>
    /// 把 CSS 颜色名（"red"/"green"/"blue"/"yellow"/"magenta"/"cyan"/"white"/"gray" 等）
    /// 解析成 <see cref="Color"/>。解析失败返回 <c>null</c>。
    /// </summary>
    public static Color? FromCssName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "red" => Color.Red,
            "brightred" => Color.BrightRed,
            "green" => Color.Green,
            "brightgreen" => Color.BrightGreen,
            "yellow" => Color.Yellow,
            "brightyellow" => Color.BrightYellow,
            "blue" => Color.Blue,
            "brightblue" => Color.BrightBlue,
            "cyan" => Color.Cyan,
            "brightcyan" => Color.BrightCyan,
            "magenta" => Color.Magenta,
            "brightmagenta" => Color.BrightMagenta,
            "white" => Color.White,
            "gray" or "grey" => Color.Gray,
            "darkgray" or "darkgrey" => Color.DarkGray,
            "black" => Color.Black,
            _ => null,
        };
    }
}
