namespace OneCode.Core.Keybindings;

/// <summary>
/// 抽象的键输入接口，用于避免对 Terminal.Gui 的直接依赖。
/// App 层提供 Terminal.Gui 的实现，Core 层仅依赖此接口。
/// </summary>
public interface IKeyInput
{
    bool Ctrl { get; }

    bool Shift { get; }

    /// <summary>终端中 Alt 和 Meta 等价（无法区分）</summary>
    bool Meta { get; }

    /// <summary>Super/Cmd/Win 修饰键，仅 kitty 协议终端可达</summary>
    bool Super { get; }

    bool IsEscape { get; }
    bool IsReturn { get; }
    bool IsTab { get; }
    bool IsBackspace { get; }
    bool IsDelete { get; }
    bool IsUpArrow { get; }
    bool IsDownArrow { get; }
    bool IsLeftArrow { get; }
    bool IsRightArrow { get; }
    bool IsPageUp { get; }
    bool IsPageDown { get; }
    bool IsHome { get; }
    bool IsEnd { get; }

    string Input { get; }
}

/// <summary>
/// 键匹配器，将 IKeyInput 与 ParsedKeystroke 进行匹配。
/// 纯函数，无状态，易于测试。
/// </summary>
public static class KeybindingMatcher
{
    /// <summary>
    /// 从 IKeyInput 提取规范化的键名。
    /// 将布尔标志映射到与 ParsedKeystroke.Key 格式一致的字符串名称。
    /// </summary>
    public static string? GetKeyName(IKeyInput keyInput)
    {
        if (keyInput.IsEscape) return "escape";
        if (keyInput.IsReturn) return "enter";
        if (keyInput.IsTab) return "tab";
        if (keyInput.IsBackspace) return "backspace";
        if (keyInput.IsDelete) return "delete";
        if (keyInput.IsUpArrow) return "up";
        if (keyInput.IsDownArrow) return "down";
        if (keyInput.IsLeftArrow) return "left";
        if (keyInput.IsRightArrow) return "right";
        if (keyInput.IsPageUp) return "pageup";
        if (keyInput.IsPageDown) return "pagedown";
        if (keyInput.IsHome) return "home";
        if (keyInput.IsEnd) return "end";
        if (keyInput.Input.Length == 1) return keyInput.Input.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// 检查 IKeyInput 的修饰键是否与 ParsedKeystroke 的修饰键匹配。
    ///
    /// Alt 和 Meta：终端中 Alt/Option 键通常被报告为 Meta。
    /// 配置中的 meta 修饰键被视为 alt 的别名——两者在 keyInput.Meta 为 true 时匹配。
    ///
    /// Super (Cmd/Win)：与 alt/meta 不同，是独立的修饰键。
    /// 仅通过 kitty 键盘协议在支持的终端上可达。
    /// </summary>
    public static bool ModifiersMatch(IKeyInput keyInput, ParsedKeystroke target)
    {
        if (keyInput.Ctrl != target.Ctrl) return false;
        if (keyInput.Shift != target.Shift) return false;

        // Alt 和 Meta 在终端中都映射到 keyInput.Meta
        var targetNeedsMeta = target.Alt || target.Meta;
        if (keyInput.Meta != targetNeedsMeta) return false;

        // Super (cmd/win) 是独立于 alt/meta 的修饰键
        if (keyInput.Super != target.Super) return false;

        return true;
    }

    /// <summary>
    /// 检查 IKeyInput 是否匹配指定的 ParsedKeystroke。
    ///
    /// 特殊处理：Escape 键按下时终端会设置 meta 标志（转义序列的遗留行为），
    /// 需要忽略 meta 修饰键，否则 "escape"（无修饰键）的绑定永远不会匹配。
    /// </summary>
    public static bool MatchesKeystroke(IKeyInput keyInput, ParsedKeystroke target)
    {
        var keyName = GetKeyName(keyInput);
        if (keyName != target.Key) return false;

        // QUIRK: 终端在按下 Escape 时会设置 meta=true（转义序列的遗留行为）。
        // 匹配 escape 键本身时需要忽略 meta 修饰键。
        if (keyInput.IsEscape)
        {
            return ModifiersMatchWithOverride(keyInput, target, metaOverride: false);
        }

        return ModifiersMatch(keyInput, target);
    }

    /// <summary>
    /// 检查 IKeyInput 是否匹配某个绑定条目的第一个按键。
    /// 仅用于单按键绑定。
    /// </summary>
    public static bool MatchesBinding(IKeyInput keyInput, KeybindingEntry binding)
    {
        if (binding.Chord.Length != 1) return false;
        var keystroke = binding.Chord[0];
        return keystroke is not null && MatchesKeystroke(keyInput, keystroke);
    }

    /// <summary>
    /// 从 IKeyInput 构建 ParsedKeystroke。
    /// </summary>
    public static ParsedKeystroke? BuildKeystroke(IKeyInput keyInput)
    {
        var keyName = GetKeyName(keyInput);
        if (keyName is null) return null;

        // QUIRK: Escape 按下时 keyInput.Meta 为 true（终端转义序列遗留行为），
        // 不应将其记录为修饰键，否则和弦匹配会失败。
        var effectiveMeta = keyInput.IsEscape ? false : keyInput.Meta;

        return new ParsedKeystroke(
            keyName,
            keyInput.Ctrl,
            effectiveMeta, // Alt
            keyInput.Shift,
            effectiveMeta, // Meta
            keyInput.Super);
    }

    /// <summary>
    /// 比较两个 ParsedKeystroke 是否相等。
    /// 将 alt/meta 折叠为一个逻辑修饰键（传统终端无法区分），
    /// 因此 "alt+k" 和 "meta+k" 是相同的键。
    /// Super (cmd/win) 是独立的。
    /// </summary>
    public static bool KeystrokesEqual(ParsedKeystroke a, ParsedKeystroke b)
    {
        return a.Key == b.Key &&
               a.Ctrl == b.Ctrl &&
               a.Shift == b.Shift &&
               (a.Alt || a.Meta) == (b.Alt || b.Meta) &&
               a.Super == b.Super;
    }

    private static bool ModifiersMatchWithOverride(IKeyInput keyInput, ParsedKeystroke target, bool metaOverride)
    {
        if (keyInput.Ctrl != target.Ctrl) return false;
        if (keyInput.Shift != target.Shift) return false;

        var targetNeedsMeta = target.Alt || target.Meta;
        if (metaOverride != targetNeedsMeta) return false;

        if (keyInput.Super != target.Super) return false;

        return true;
    }
}
