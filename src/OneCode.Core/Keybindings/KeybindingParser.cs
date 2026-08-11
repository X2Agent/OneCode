namespace OneCode.Core.Keybindings;

/// <summary>
/// 快捷键字符串解析器，将 "ctrl+shift+k" 等字符串解析为 ParsedKeystroke。
/// 纯函数，无状态，易于测试。
/// </summary>
public static class KeybindingParser
{
    /// <summary>
    /// 解析单个按键字符串（如 "ctrl+shift+k"）为 ParsedKeystroke。
    /// </summary>
    public static ParsedKeystroke ParseKeystroke(string input)
    {
        var parts = input.Split('+');
        var key = string.Empty;
        var ctrl = false;
        var alt = false;
        var shift = false;
        var meta = false;
        var super = false;

        foreach (var part in parts)
        {
            var lower = part.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "alt":
                case "opt":
                case "option":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "meta":
                    meta = true;
                    break;
                case "cmd":
                case "command":
                case "super":
                case "win":
                    super = true;
                    break;
                case "esc":
                case "escape":
                    key = "escape";
                    break;
                case "return":
                case "enter":
                    key = "enter";
                    break;
                case "space":
                    key = " ";
                    break;
                case "tab":
                    key = "tab";
                    break;
                case "backspace":
                    key = "backspace";
                    break;
                case "delete":
                case "del":
                    key = "delete";
                    break;
                case "up":
                case "\u2191":
                    key = "up";
                    break;
                case "down":
                case "\u2193":
                    key = "down";
                    break;
                case "left":
                case "\u2190":
                    key = "left";
                    break;
                case "right":
                case "\u2192":
                    key = "right";
                    break;
                case "pageup":
                case "pgup":
                    key = "pageup";
                    break;
                case "pagedown":
                case "pgdn":
                    key = "pagedown";
                    break;
                case "home":
                    key = "home";
                    break;
                case "end":
                    key = "end";
                    break;
                case "insert":
                case "ins":
                    key = "insert";
                    break;
                case "wheelup":
                    key = "wheelup";
                    break;
                case "wheeldown":
                    key = "wheeldown";
                    break;
                default:
                    key = lower;
                    break;
            }
        }

        return new ParsedKeystroke(key, ctrl, alt, shift, meta, super);
    }

    /// <summary>
    /// 解析和弦序列字符串（如 "ctrl+x ctrl+k"）为 ParsedKeystroke 数组。
    /// 单独的空格字符被视为 space 键绑定，而非分隔符。
    /// </summary>
    public static ParsedKeystroke[] ParseChord(string input)
    {
        if (input == " ")
            return [ParseKeystroke("space")];

        return input.Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseKeystroke)
            .ToArray();
    }

    /// <summary>
    /// 将 ParsedKeystroke 转换为规范字符串表示（用于显示）。
    /// </summary>
    public static string KeystrokeToString(ParsedKeystroke ks)
    {
        var parts = new List<string>();
        if (ks.Ctrl) parts.Add("ctrl");
        if (ks.Alt) parts.Add("alt");
        if (ks.Shift) parts.Add("shift");
        if (ks.Meta) parts.Add("meta");
        if (ks.Super) parts.Add("cmd");
        parts.Add(KeyToDisplayName(ks.Key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// 将和弦序列转换为规范字符串表示。
    /// </summary>
    public static string ChordToString(ParsedKeystroke[] chord)
    {
        return string.Join(" ", chord.Select(KeystrokeToString));
    }

    /// <summary>
    /// 将 ParsedKeystroke 转换为平台适配的显示字符串。
    /// macOS 显示 opt/cmd，其他平台显示 alt/super。
    /// </summary>
    public static string KeystrokeToDisplayString(ParsedKeystroke ks, DisplayPlatform platform = DisplayPlatform.Linux)
    {
        var parts = new List<string>();
        if (ks.Ctrl) parts.Add("ctrl");
        if (ks.Alt || ks.Meta)
        {
            parts.Add(platform == DisplayPlatform.MacOS ? "opt" : "alt");
        }
        if (ks.Shift) parts.Add("shift");
        if (ks.Super)
        {
            parts.Add(platform == DisplayPlatform.MacOS ? "cmd" : "super");
        }
        parts.Add(KeyToDisplayName(ks.Key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// 将和弦序列转换为平台适配的显示字符串。
    /// </summary>
    public static string ChordToDisplayString(ParsedKeystroke[] chord, DisplayPlatform platform = DisplayPlatform.Linux)
    {
        return string.Join(" ", chord.Select(ks => KeystrokeToDisplayString(ks, platform)));
    }

    /// <summary>
    /// 将 JSON 配置中的绑定块解析为扁平的 KeybindingEntry 列表。
    /// </summary>
    public static List<KeybindingEntry> ParseBindings(IEnumerable<KeybindingBlock> blocks)
    {
        var bindings = new List<KeybindingEntry>();
        foreach (var block in blocks)
        {
            foreach (var (key, action) in block.Bindings)
            {
                bindings.Add(new KeybindingEntry(
                    block.Context,
                    ParseChord(key),
                    action));
            }
        }
        return bindings;
    }

    /// <summary>
    /// 规范化按键字符串用于比较（小写、排序修饰键）。
    /// 和弦序列按步骤分别规范化。
    /// </summary>
    public static string NormalizeKeyForComparison(string key)
    {
        return string.Join(" ",
            key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeStep));
    }

    private static string NormalizeStep(string step)
    {
        var parts = step.Split('+');
        var modifiers = new List<string>();
        var mainKey = string.Empty;

        foreach (var part in parts)
        {
            var lower = part.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    modifiers.Add("ctrl");
                    break;
                case "alt":
                case "opt":
                case "option":
                    modifiers.Add("alt");
                    break;
                case "shift":
                    modifiers.Add("shift");
                    break;
                case "meta":
                    modifiers.Add("meta");
                    break;
                case "cmd":
                case "command":
                case "super":
                case "win":
                    modifiers.Add("cmd");
                    break;
                default:
                    mainKey = lower;
                    break;
            }
        }

        modifiers.Sort();
        return string.Join("+", [.. modifiers, mainKey]);
    }

    private static string KeyToDisplayName(string key) => key switch
    {
        "escape" => "Esc",
        " " => "Space",
        "tab" => "Tab",
        "enter" => "Enter",
        "backspace" => "Backspace",
        "delete" => "Delete",
        "up" => "\u2191",
        "down" => "\u2193",
        "left" => "\u2190",
        "right" => "\u2192",
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "home" => "Home",
        "end" => "End",
        "insert" => "Insert",
        _ => key,
    };
}
