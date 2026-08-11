namespace OneCode.App.Tui;

/// <summary>
/// 统一颜色方案 — 对标 TypeScript Ink 版本的暗色主题。
/// 所有 TUI 组件从这里取 Scheme，不在组件内硬编码颜色。
///
/// 颜色常量定义在 <see cref="TuiPalette"/>；本类仅提供 Scheme（Normal/Focus/HotNormal/HotFocus/Disabled 组合）
/// 和少数无法用单一 Color 表达的 Attribute（如 spinner、thinking 文本）。
/// </summary>
internal static class TuiTheme
{
    // Base
    public static Scheme Base => new()
    {
        Normal = new Attribute(Color.Gray, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.BrightGreen, Color.Black),
        HotFocus = new Attribute(Color.BrightGreen, Color.Black),
        Disabled = new Attribute(Color.DarkGray, Color.Black),
    };

    // Conversation pane
    public static Scheme ConversationArea => new()
    {
        Normal = new Attribute(Color.White, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.White, Color.Black),
        HotFocus = new Attribute(Color.White, Color.Black),
        Disabled = new Attribute(Color.DarkGray, Color.Black),
    };

    // Message roles
    public static Attribute UserLine => new(Color.BrightGreen, Color.Black);
    public static Attribute AssistantLine => new(Color.White, Color.Black);
    public static Attribute StreamingLine => new(Color.BrightBlue, Color.Black);
    public static Attribute ToolLine => new(Color.BrightYellow, Color.Black);
    public static Attribute ErrorLine => new(Color.BrightRed, Color.Black);
    public static Attribute SystemLine => new(Color.DarkGray, Color.Black);
    public static Attribute ThinkingLine => new(Color.Cyan, Color.Black);
    public static Attribute SpinnerColor => new(Color.BrightCyan, Color.Black);

    // Chat input region
    public static Scheme ChatInput => new()
    {
        Normal = new Attribute(Color.White, Color.Black),
        Focus = new Attribute(Color.White, Color.Black),
        HotNormal = new Attribute(Color.BrightGreen, Color.Black),
        HotFocus = new Attribute(Color.BrightGreen, Color.Black),
        Disabled = new Attribute(Color.DarkGray, Color.Black),
    };

    // Status bar
    public static Scheme StatusBar => new()
    {
        Normal = new Attribute(Color.DarkGray, Color.Black),
        Focus = new Attribute(Color.DarkGray, Color.Black),
        HotNormal = new Attribute(Color.DarkGray, Color.Black),
        HotFocus = new Attribute(Color.DarkGray, Color.Black),
        Disabled = new Attribute(Color.DarkGray, Color.Black),
    };

    // Completion popup
    public static Scheme Completion => new()
    {
        Normal = new Attribute(Color.White, Color.DarkGray),
        Focus = new Attribute(Color.Black, Color.Gray),
        HotNormal = new Attribute(Color.White, Color.DarkGray),
        HotFocus = new Attribute(Color.Black, Color.Gray),
        Disabled = new Attribute(Color.DarkGray, Color.DarkGray),
    };

    // Modals
    public static Scheme Modal => new()
    {
        Normal = new Attribute(Color.White, Color.Black),
        Focus = new Attribute(Color.White, Color.DarkGray),
        HotNormal = new Attribute(Color.BrightCyan, Color.Black),
        HotFocus = new Attribute(Color.BrightCyan, Color.DarkGray),
        Disabled = new Attribute(Color.Gray, Color.Black),
    };

    // Scheme factories
    // 通用 Scheme 构造工具，供任何 View（不仅限于 CenteredOverlay 子类）使用。

    /// <summary>
    /// Creates a uniform Scheme for all states from a single fg/bg pair.
    /// Use for static labels where no hot-key highlight is needed.
    /// </summary>
    public static Scheme MakeScheme(Color fg, Color bg)
    {
        var attr = new Attribute(fg, bg);
        return new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Disabled = new Attribute(TuiPalette.FgMuted, bg),
        };
    }

    /// <summary>
    /// Creates an interactive-field Scheme: Normal/Focus use <paramref name="fg"/>,
    /// HotNormal/HotFocus use the accent colour, Disabled uses FgMuted.
    /// Use for ListViews, TextFields, and other interactive fields that share
    /// the standard accent-on-hot pattern.
    /// </summary>
    public static Scheme MakeFieldScheme(Color fg, Color bg)
    {
        var normal = new Attribute(fg, bg);
        var hot = new Attribute(TuiPalette.Accent, bg);
        return new Scheme
        {
            Normal = normal,
            Focus = normal,
            HotNormal = hot,
            HotFocus = hot,
            Disabled = new Attribute(TuiPalette.FgMuted, bg),
        };
    }

    /// <summary>
    /// Creates a ListView-friendly Scheme with a visible selection highlight.
    /// Normal: fg on bg. Focus: fg on <see cref="TuiPalette.BgActive"/> (elevated background).
    /// HotNormal/HotFocus: accent on the corresponding background.
    /// Use for <see cref="ListView"/> and similar selectable-list controls where the
    /// built-in <see cref="MakeFieldScheme"/> provides no visual selection feedback.
    /// </summary>
    public static Scheme MakeListScheme(Color fg, Color bg)
    {
        var normal = new Attribute(fg, bg);
        var focus = new Attribute(fg, TuiPalette.BgActive);
        var hotNormal = new Attribute(TuiPalette.Accent, bg);
        var hotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgActive);
        return new Scheme
        {
            Normal = normal,
            Focus = focus,
            HotNormal = hotNormal,
            HotFocus = hotFocus,
            Disabled = new Attribute(TuiPalette.FgMuted, bg),
        };
    }

    /// <summary>
    /// Creates a button-style Scheme: Normal uses <paramref name="bgNormal"/>,
    /// Focus uses <paramref name="bgFocus"/> (visually elevated), Hot states use
    /// the accent colour on the corresponding background.
    /// </summary>
    public static Scheme MakeButtonScheme(Color fg, Color bgNormal, Color bgFocus)
    {
        return new Scheme
        {
            Normal = new Attribute(fg, bgNormal),
            Focus = new Attribute(fg, bgFocus),
            HotNormal = new Attribute(TuiPalette.Accent, bgNormal),
            HotFocus = new Attribute(TuiPalette.Accent, bgFocus),
            Disabled = new Attribute(TuiPalette.FgMuted, bgNormal),
        };
    }
}
