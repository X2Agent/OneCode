namespace OneCode.App.Tui;

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

    /// <summary>复制图标（⧉），用于代码块标题栏的复制按钮。U+29C9 TWO JOINED SQUARES，兼容性好且宽度为 1 cell。</summary>
    public const string CopyIcon = "⧉";

    // 角色与状态指示
    public const string RoleBullet = "●";

    public const string BrandMark = "◆";

    public const string Lightning = "⚡";

    public const string BarQuote = "▎";

    // 块字符
    public const string BlockFull = "█";

    public const string BlockLight = "░";

    // 边框绘制字符（方角单线，DESIGN.md 规范）
    public const string BorderTopLeft = "┌";
    public const string BorderTopRight = "┐";
    public const string BorderBottomLeft = "└";
    public const string BorderBottomRight = "┘";
    public const string BorderHorizontal = "─";
    public const string BorderVertical = "│";
    public const string BorderLeftTee = "├";
    public const string BorderRightTee = "┤";
    public const string BorderTopTee = "┬";
    public const string BorderBottomTee = "┴";
    public const string BorderCross = "┼";
}
