namespace OneCode.App.Tui;

/// <summary>
/// 集中管理 TUI 的布局间距与尺寸常量，落地 DESIGN.md §spacing 定义的 token 体系。
///
/// DESIGN.md 以整数字符单元格（列/行）度量（Compact &lt;100 / Standard 100~140 / Wide &gt;140）。
/// Xs/Sm/Md 为间距刻度（仅 Overlay/Form/按钮间隙使用），不得用于新的行高计算。
///
/// 所有 TUI 组件的 padding/margin/起点坐标都应从这里取值，禁止散落硬编码。
/// </summary>
internal static class TuiSpacing
{
    // 响应式断点（DESIGN.md §2）
    /// <summary>紧凑屏断点下限（列）：低于 100 列为单栏紧凑模式（Compact）。</summary>
    public const int BreakpointCompact = 100;

    /// <summary>宽屏断点下限（列）：高于 140 列为并列双栏宽屏模式（Wide）。</summary>
    public const int BreakpointWide = 140;

    /// <summary>主会话区保底宽度（列）。</summary>
    public const int ChatColumnMinWidth = 65;

    // 基础间距 token（DESIGN.md §spacing）
    public const int Xs = 1;

    public const int Sm = 2;

    public const int Md = 4;

    // Overlay 内容起点（统一规则）
    public const int OverlayContentX = 3;

    public const int OverlayContentY = 3;

    public const int OverlayHeaderX = 2;

    public const int OverlayHeaderY = 1;

    public const int StatusBarHeight = 1;

    /// <summary>上下文栏高度（git 分支信息，非 DESIGN.md 标准层）。</summary>
    public const int SessionContextBarHeight = 1;

    // 消息流缩进
    public const int MessageContentIndent = 2;

    /// <summary>消息头时间戳与右侧应用内滚动条之间的安全间距。</summary>
    public const int MessageTimestampRightPadding = Sm;

    /// <summary>
    /// Fallback width when the viewport has not been measured yet (e.g. before
    /// the first draw). Real draws always pass the live viewport width.
    /// </summary>
    public const int DefaultContentWidth = 80;

    /// <summary>
    /// 输入框总高度动态上限：Math.Clamp(屏幕高度 / 6, 2, <see cref="ChatInputView.MaxHeight"/>)。
    /// 24 行终端上限 4 行（1 分隔线 + 3 编辑行），40+ 行终端可优雅展开至 6 行，
    /// 兑现「输入框高度不超过屏幕约 1/6」的小屏承诺。
    /// </summary>
    public static int GetInputMaxTotalHeight(int screenHeight)
        => Math.Clamp(screenHeight / 6, 2, ChatInputView.MaxHeight);

    /// <summary>
    /// Width used when wrapping / rendering chat lines for a given viewport.
    /// Tracks the viewport 1:1 so maximized terminals fill available space
    /// instead of leaving large empty side gutters.
    /// </summary>
    public static int GetContentColumnWidth(int viewportWidth)
        => viewportWidth <= 0 ? DefaultContentWidth : viewportWidth;

    // Overlay 默认尺寸（CenteredOverlay 基类）
    public const int OverlayDefaultWidth = 60;

    public const int OverlayDefaultHeight = 16;

    // 表单字段
    public const int FormLabelWidth = 14;

    public const int FormFieldX = FormLabelWidth + 1;

    // MCP 配置页（McpConfigOverlay）双栏布局
    /// <summary>MCP 配置页服务器窗格常规宽度（窄对话框下收缩）。</summary>
    public const int McpServerPaneWidth = 36;

    /// <summary>MCP 配置页服务器窗格收缩下限（为方法窗格让位）。</summary>
    public const int McpServerPaneMinWidth = 8;

    /// <summary>MCP 配置页方法窗格最小宽度，低于该宽度勾选列表不可用。</summary>
    public const int McpToolPaneMinWidth = 20;

    /// <summary>MCP 配置页左右窗格间隔。</summary>
    public const int McpPaneGap = 2;

    // 主布局预留
    /// <summary>对话区与 AgentStatusBar 之间的空行间距（已优化为 0）。</summary>
    public const int StatusBarTopGap = 0;

    /// <summary>InputBar 与 ContextBar 之间的空行间距（已优化为 0）。</summary>
    public const int ChatInputContextGap = 0;

    /// <summary>
    /// ContentZone 底部预留高度基线（单行输入时的紧凑高度）：
    /// 会话上下文栏 1 + Agent 状态栏 1 + 两处间距各 0 + 单行聊天输入区高度 2 = 4。
    /// 当输入框输入多行时，动态根据实际高度调整（上限见 <see cref="ChatInputView.MaxHeight"/>，即 6 行）。
    /// </summary>
    public const int ContentZoneReservedBottom =
        SessionContextBarHeight + StatusBarHeight + StatusBarTopGap + ChatInputContextGap + ChatInputView.MinHeight;
}
