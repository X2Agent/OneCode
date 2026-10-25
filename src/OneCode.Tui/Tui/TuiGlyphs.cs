namespace OneCode.Tui;

/// <summary>
/// 集中管理 TUI 的符号常量。所有 Unicode 符号字符都应通过此处的常量引用，
/// 禁止在代码中散落硬编码符号字符（避免同一概念使用不同符号的不一致问题）。
/// </summary>
internal static class TuiGlyphs
{
    // 状态符号
    public const string InProgress = "◈";

    public const string Complete = "✓";

    public const string Failed = "✗";

    public const string Pending = "○";

    public const string Active = "◉";

    public const string Ready = "◯";

    public const string Paused = "⏸";

    // 流程与方向
    public const string Collapsed = "▶";

    public const string Expanded = "▼";

    public const string ToolCall = "▸";

    public const string ToolResultPrefix = "↳";

    public const string ArrowRight = "→";

    public const string ArrowLeft = "←";

    public const string ArrowUp = "↑";

    public const string ArrowDown = "↓";

    /// <summary>省略号（…），表示文本截断或"更多"。采用 U+2026 标准文本省略号以获得最佳字体支持。</summary>
    public const string Ellipsis = "…";

    /// <summary>流式输出尾部光标（▌），用于实时 token 预览末尾指示生成位置。</summary>
    public const string StreamingCursor = "▌";

    // 角色与状态指示
    public const string RoleBullet = "●";

    public const string BrandMark = "◆";

    public const string Lightning = "⚡";

    public const string BarQuote = "▎";

    // 块字符
    public const string BlockFull = "█";

    public const string BlockLight = "░";

    // 边框绘制字符（单线横竖与 Tee 接头；外框角字符用下方圆角套件）
    public const string BorderHorizontal = "─";
    public const string BorderVertical = "│";
    public const string BorderLeftTee = "├";
    public const string BorderRightTee = "┤";

    // 圆角单线边框角字符（DESIGN.md 规范：Overlay 与代码块外框用圆角）
    public const string RoundedBorderTopLeft = "╭";
    public const string RoundedBorderTopRight = "╮";
    public const string RoundedBorderBottomLeft = "╰";
    public const string RoundedBorderBottomRight = "╯";
}
