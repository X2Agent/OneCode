namespace OneCode.App.Tui;

/// <summary>
/// 集中管理 TUI 的布局间距与尺寸常量，落地 DESIGN.md §spacing 定义的 token 体系。
///
/// DESIGN.md 定义的是 px 值（xs=2/sm=4/md=8/lg=12/xl=16）。
/// 终端按字符计算，1px ≈ 1 cell 的水平间距；垂直间距通常用行数表达。
/// 此处将 px 值近似为终端字符数（向下取整为最接近的整数 cell）。
///
/// 所有 TUI 组件的 padding/margin/起点坐标都应从这里取值，禁止散落硬编码。
/// </summary>
internal static class TuiSpacing
{
    // 基础间距 token（DESIGN.md §spacing）
    public const int Xs = 1;

    public const int Sm = 2;

    public const int Md = 4;

    public const int Lg = 6;

    public const int Xl = 8;

    // Overlay 内容起点（统一规则）
    public const int OverlayContentX = 3;

    public const int OverlayContentY = 3;

    public const int OverlayHeaderX = 2;

    public const int OverlayHeaderY = 1;

    // 标题栏 / 状态栏 / 输入栏
    public const int BarPaddingX = 1;

    public const int StatusBarHeight = 1;

    /// <summary>上下文栏高度（git 分支信息，非 DESIGN.md 标准层）。</summary>
    public const int SessionContextBarHeight = 1;

    // 消息流缩进
    public const int MessageContentIndent = 2;

    /// <summary>消息头时间戳与右侧应用内滚动条之间的安全间距。</summary>
    public const int MessageTimestampRightPadding = Sm;

    public const int MessageSpacing = 1;

    /// <summary>
    /// Fallback width when the viewport has not been measured yet (e.g. before
    /// the first draw). Real draws always pass the live viewport width.
    /// </summary>
    public const int DefaultContentWidth = 80;

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

    public const int OverlayMaxHeight = 28;

    // 按钮与交互元素
    public const int ButtonPaddingX = 1;

    public const int ButtonGap = 2;

    // 表单字段
    public const int FormLabelWidth = 14;

    public const int FormFieldX = FormLabelWidth + 1;

    // 主布局预留
    /// <summary>对话区与 AgentStatusBar 之间的空行间距。</summary>
    public const int StatusBarTopGap = 1;

    /// <summary>InputBar 与 ContextBar 之间的空行间距。</summary>
    public const int ChatInputContextGap = 1;

    /// <summary>
    /// ContentZone 底部预留高度 =
    /// 会话上下文栏 1 + Agent 状态栏 1 + 两处间距各 1 + 聊天输入区最大高度 5 = 9。
    /// 聊天输入区最大高度包含 1 行分隔线和最多 4 行文本编辑器。
    /// </summary>
    public const int ContentZoneReservedBottom =
        SessionContextBarHeight + StatusBarHeight + StatusBarTopGap + ChatInputContextGap + ChatInputView.MaxHeight;
}
