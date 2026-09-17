namespace OneCode.App.Tui;

/// <summary>
/// 统一静态控件样式。
/// 所有 TUI 组件从这里取 Scheme，不在组件内硬编码颜色。
///
/// 颜色常量定义在 <see cref="TuiPalette"/>；本类仅提供 Scheme（Normal/Focus/HotNormal/HotFocus/Disabled 组合）
/// 和少数无法用单一 Color 表达的 Attribute（如 spinner、thinking 文本）。
/// </summary>
internal static class TuiStyles
{
    // Base
    public static Scheme Base => new()
    {
        Normal = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary),
        Focus = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgPrimary),
        HotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgPrimary),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary),
    };

    // Conversation pane
    public static Scheme ConversationArea => new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary),
        Focus = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary),
        HotNormal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary),
        HotFocus = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary),
    };

    // Message roles
    public static Attribute UserLine => new(TuiPalette.UserMessage, TuiPalette.BgPrimary);
    public static Attribute AssistantLine => new(TuiPalette.AssistantMessage, TuiPalette.BgPrimary);
    public static Attribute StreamingLine => new(TuiPalette.Streaming, TuiPalette.BgPrimary);
    public static Attribute ToolLine => new(TuiPalette.ToolUse, TuiPalette.BgPrimary);
    public static Attribute ErrorLine => new(TuiPalette.Error, TuiPalette.BgPrimary);
    public static Attribute SystemLine => new(TuiPalette.FgMuted, TuiPalette.BgPrimary);
    public static Attribute ThinkingLine => new(TuiPalette.Thinking, TuiPalette.BgPrimary);
    public static Attribute SpinnerColor => new(TuiPalette.Accent, TuiPalette.BgPrimary);

    // Chat input region — 背景与状态栏一致（BgSurface），使底部区域视觉连贯
    public static Scheme ChatInput => new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgSurface),
        Focus = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgSurface),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgSurface),
        HotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgSurface),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgSurface),
    };

    // Status bar
    public static Scheme StatusBar => new()
    {
        Normal = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgSurface),
        Focus = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgSurface),
        HotNormal = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgSurface),
        HotFocus = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgSurface),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgSurface),
    };

    // Completion popup
    public static Scheme Completion => new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgCard),
        Focus = new Attribute(TuiPalette.BgPrimary, TuiPalette.FgSecondary),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotFocus = new Attribute(TuiPalette.BgPrimary, TuiPalette.Accent),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgCard),
    };

    // Modals
    public static Scheme Modal => new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgCard),
        Focus = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgActive),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgActive),
        Disabled = new Attribute(TuiPalette.FgSecondary, TuiPalette.BgCard),
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
