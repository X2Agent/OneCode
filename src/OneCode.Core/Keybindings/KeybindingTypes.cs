namespace OneCode.Core.Keybindings;

/// <summary>
/// 快捷键解析结果，表示一个按键及其修饰键组合。
/// </summary>
public sealed record ParsedKeystroke(
    string Key,
    bool Ctrl,
    bool Alt,
    bool Shift,
    bool Meta,
    bool Super);

/// <summary>
/// 绑定条目，表示一个上下文中的完整快捷键绑定。
/// Chord 支持和弦序列（如 ctrl+x ctrl+k）。
/// Action 为 null 表示显式解绑。
/// </summary>
public sealed record KeybindingEntry(
    string Context,
    ParsedKeystroke[] Chord,
    string? Action);

/// <summary>
/// 快捷键解析结果枚举。
/// </summary>
public enum KeyResolveResult
{
    None,
    Match,
    Unbound,
    ChordStarted,
    ChordCancelled,
}

/// <summary>
/// 快捷键解析返回结果。
/// </summary>
public sealed record KeyResolveReturn(
    KeyResolveResult Result,
    string? Action = null);

/// <summary>
/// JSON 配置文件中的绑定块结构。
/// </summary>
public sealed record KeybindingBlock(
    string Context,
    Dictionary<string, string?> Bindings);

/// <summary>
/// 验证警告类型。
/// </summary>
public enum KeybindingWarningType
{
    ParseError,
    Duplicate,
    Reserved,
    InvalidContext,
    InvalidAction,
}

/// <summary>
/// 验证警告严重级别。
/// </summary>
public enum KeybindingSeverity
{
    Error,
    Warning,
}

/// <summary>
/// 验证警告条目。
/// </summary>
public sealed record KeybindingWarning(
    KeybindingWarningType Type,
    KeybindingSeverity Severity,
    string Message,
    string? Key = null,
    string? Context = null,
    string? Action = null,
    string? Suggestion = null);

/// <summary>
/// 保留快捷键条目。
/// </summary>
public sealed record ReservedShortcut(
    string Key,
    string Reason,
    KeybindingSeverity Severity);

/// <summary>
/// 键绑定加载结果。
/// </summary>
public sealed record KeybindingsLoadResult(
    KeybindingEntry[] Bindings,
    KeybindingWarning[] Warnings);

/// <summary>
/// 平台类型，用于显示适配。
/// </summary>
public enum DisplayPlatform
{
    MacOS,
    Windows,
    Linux,
}
