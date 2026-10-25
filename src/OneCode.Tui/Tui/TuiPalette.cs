namespace OneCode.Tui;

/// <summary>
/// 集中管理 TUI 的所有颜色常量，使用 Terminal.Gui v2 的 24-bit TrueColor。
/// 终端不支持 TrueColor 时 Terminal.Gui 自动降级到最接近的 16 色。
///
/// 配色来源：code.pen 设计稿 hex 值。
/// </summary>
internal static class TuiPalette
{
    /// <summary>Design MD 使用的完整语义颜色 token 集合。</summary>
    internal static IReadOnlyDictionary<string, string> DesignTokens { get; } = new Dictionary<string, string>
    {
        ["accent"] = "#4D94FF",
        ["accent-dim"] = "#2F7BE0",
        ["success"] = "#22C55E",
        ["error"] = "#EF4444",
        ["warning"] = "#F59E0B",
        ["info"] = "#58A6FF",
        ["in-progress"] = "#4D94FF",
        ["streaming"] = "#8B949E",
        ["thinking"] = "#5BB8C8",
        ["fg-primary"] = "#F5F7FA",
        ["fg-secondary"] = "#8B949E",
        ["fg-muted"] = "#6B7280",
        ["bg-primary"] = "#0D0D0D",
        ["bg-input"] = "#131313",
        ["bg-terminal"] = "#161616",
        ["bg-terminal-header"] = "#1A1A1A",
        ["bg-card"] = "#131313",
        ["bg-error"] = "#1A0A0A",
        ["bg-success"] = "#0A160A",
        ["border"] = "#242424",
        ["border-accent"] = "#4D94FF",
        ["separator"] = "#242424",
        ["user-message"] = "#58A6FF",
        ["assistant-message"] = "#F5F7FA",
        ["tool-use"] = "#F59E0B",
        ["tool-result"] = "#D29922",
        ["system-message"] = "#BC8CFF",
        ["diff-added"] = "#22C55E",
        ["diff-removed"] = "#EF4444",
        ["diff-hunk"] = "#4D94FF",
        ["diff-context"] = "#8B949E",
        ["mode-build"] = "#4CAF84",
        ["mode-plan"] = "#5B8DEE",
        ["mode-goal"] = "#5BB8C8",
        ["tool-detail"] = "#8B949E",
        ["thought-timing"] = "#D29922",
    };

    // 品牌主色
    /// <summary>
    /// 终端交互驱动色（#4D94FF）。品牌基色为 #0066FF（与 WebUI 一致），
    /// 但其在本方案暗底（bg-primary #0D0D0D）上作前景对比度不足，
    /// 故终端一律使用可读亮阶——同 WebUI 的 <c>--accent-text</c>。
    /// </summary>
    public static readonly Color Accent = new(0x4D, 0x94, 0xFF);

    /// <summary>品牌次色，用于较弱高亮（如 hover/focus 弱态）。#2F7BE0。</summary>
    public static readonly Color AccentDim = new(0x2F, 0x7B, 0xE0);

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
    public static readonly Color InProgress = new(0x4D, 0x94, 0xFF);

    /// <summary>流式输出（次级文本）。#8B949E。</summary>
    public static readonly Color Streaming = new(0x8B, 0x94, 0x9E);

    /// <summary>思考过程文本。#5BB8C8。</summary>
    public static readonly Color Thinking = new(0x5B, 0xB8, 0xC8);

    // 文本色
    /// <summary>主前景（默认正文）。#F5F7FA。</summary>
    public static readonly Color FgPrimary = new(0xF5, 0xF7, 0xFA);

    /// <summary>次前景（描述/说明）。#8B949E。</summary>
    public static readonly Color FgSecondary = new(0x8B, 0x94, 0x9E);

    /// <summary>弱前景（分隔线/弱提示）。#6B7280。</summary>
    public static readonly Color FgMuted = new(0x6B, 0x72, 0x80);

    // 背景色
    /// <summary>主背景。#0D0D0D。</summary>
    public static readonly Color BgPrimary = new(0x0D, 0x0D, 0x0D);

    /// <summary>输入区与待办横条背景。#131313——
    /// 中性微亮深灰，与消息区（BgPrimary #0D0D0D）只保留微弱分层。</summary>
    public static readonly Color BgInput = new(0x13, 0x13, 0x13);

    /// <summary>终端背景。#161616。</summary>
    public static readonly Color BgTerminal = new(0x16, 0x16, 0x16);

    /// <summary>终端头部。#1A1A1A。</summary>
    public static readonly Color BgTerminalHeader = new(0x1A, 0x1A, 0x1A);

    /// <summary>卡片背景。#131313。</summary>
    public static readonly Color BgCard = new(0x13, 0x13, 0x13);

    /// <summary>错误面板背景。#1A0A0A。</summary>
    public static readonly Color BgError = new(0x1A, 0x0A, 0x0A);

    /// <summary>成功面板背景。#0A160A。</summary>
    public static readonly Color BgSuccess = new(0x0A, 0x16, 0x0A);

    // UI 框架色
    /// <summary>普通边框色。#242424。</summary>
    public static readonly Color Border = new(0x24, 0x24, 0x24);

    /// <summary>强调边框色（用于对话框、终端头部）。</summary>
    public static readonly Color BorderAccent = new(0x4D, 0x94, 0xFF);

    /// <summary>分隔线色。</summary>
    public static readonly Color Separator = new(0x24, 0x24, 0x24);

    // 角色色（消息流）
    /// <summary>用户消息。#58A6FF。</summary>
    public static readonly Color UserMessage = new(0x58, 0xA6, 0xFF);

    /// <summary>助手消息。#F5F7FA。</summary>
    public static readonly Color AssistantMessage = new(0xF5, 0xF7, 0xFA);

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

    /// <summary>Diff Hunk header（@@ -12,14 +12,12 @@）。#4D94FF。</summary>
    public static readonly Color DiffHunk = new(0x4D, 0x94, 0xFF);

    /// <summary>Diff 上下文行。#8B949E。</summary>
    public static readonly Color DiffContext = new(0x8B, 0x94, 0x9E);

    // 模式色（工作模式标签）
    /// <summary>Build 模式前景色。#4CAF84。</summary>
    public static readonly Color ModeBuildFg = new(0x4C, 0xAF, 0x84);

    /// <summary>Plan 模式前景色。#5B8DEE。</summary>
    public static readonly Color ModePlanFg = new(0x5B, 0x8D, 0xEE);


    /// <summary>Goal 模式前景色。#5BB8C8。</summary>
    public static readonly Color ModeGoalFg = new(0x5B, 0xB8, 0xC8);

    // Agent 8-色系统
    /// <summary>orchestrator — 紫色 #A386D8。</summary>
    public static readonly Color AgentPurple = new(0xA3, 0x86, 0xD8);
    /// <summary>researcher — 蓝色 #5B8DEE，终端中仅作角色色使用。</summary>
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

    /// <summary>活跃背景色。#1A1A1A。</summary>
    public static readonly Color BgActive = new(0x1A, 0x1A, 0x1A);

    /// <summary>状态正常色。</summary>
    public static readonly Color StatusOk = new(0x22, 0xC5, 0x5E);

    // 消息美化新增色
    /// <summary>工具详情行（args、duration）。#8B949E。</summary>
    public static readonly Color ToolDetailColor = new(0x8B, 0x94, 0x9E);

    /// <summary>Thinking 耗时标识色。#D29922。</summary>
    public static readonly Color ThoughtTimingColor = new(0xD2, 0x99, 0x22);

    /// <summary>根据当前工作模式返回用户消息 ▎ 色（Build=绿, Plan=蓝, Goal=青）。</summary>
    public static Color GetModeBarColor(WorkingMode mode) => mode switch
    {
        WorkingMode.Build => ModeBuildFg,
        WorkingMode.Plan => ModePlanFg,
        WorkingMode.Goal => ModeGoalFg,
        _ => Accent,
    };

    /// <summary>
    /// 根据 agent 名称返回对应的前景色（八色系统）。
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
}
