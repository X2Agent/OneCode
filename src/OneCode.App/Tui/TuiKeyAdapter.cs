using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Adapts Terminal.Gui's <see cref="Key"/> to the Core layer's <see cref="IKeyInput"/> interface.
/// Bridges the TUI keyboard events with the KeybindingResolver system.
/// </summary>
public sealed class TuiKeyAdapter : IKeyInput
{
    private readonly Key _key;

    public TuiKeyAdapter(Key key)
    {
        _key = key;
    }

    public bool Ctrl => _key.IsCtrl;
    public bool Shift => _key.IsShift;
    public bool Meta => _key.IsAlt;
    public bool Super => false; // Terminal.Gui v2 does not expose Super/Cmd/Win

    // Strip modifiers: terminals often report Escape with Meta/Alt set (legacy
    // CSI encoding). Strict `_key == Key.Esc` then fails, so chat:cancel never
    // matches and Esc appears dead while the prompt has focus.
    public bool IsEscape => _key.NoShift.NoCtrl.NoAlt == Key.Esc;
    public bool IsReturn => _key.NoShift.NoCtrl.NoAlt == Key.Enter;
    // IsTab 需要剥离修饰键后比较基础键，否则 Shift+Tab 不会被识别为 Tab 键，
    // 导致 "shift+tab" → ActionChatToggleStrategy 的键绑定永远无法匹配。
    // 修饰键（Shift/Ctrl/Alt）由 KeybindingMatcher.ModifiersMatch 单独检查。
    public bool IsTab => _key.NoShift.NoCtrl.NoAlt == Key.Tab;
    public bool IsBackspace => _key.NoShift.NoCtrl.NoAlt == Key.Backspace;
    public bool IsDelete => _key.NoShift.NoCtrl.NoAlt == Key.Delete;
    public bool IsUpArrow => _key.NoShift.NoCtrl.NoAlt == Key.CursorUp;
    public bool IsDownArrow => _key.NoShift.NoCtrl.NoAlt == Key.CursorDown;
    public bool IsLeftArrow => _key.NoShift.NoCtrl.NoAlt == Key.CursorLeft;
    public bool IsRightArrow => _key.NoShift.NoCtrl.NoAlt == Key.CursorRight;
    public bool IsPageUp => _key.NoShift.NoCtrl.NoAlt == Key.PageUp;
    public bool IsPageDown => _key.NoShift.NoCtrl.NoAlt == Key.PageDown;
    public bool IsHome => _key.NoShift.NoCtrl.NoAlt == Key.Home;
    public bool IsEnd => _key.NoShift.NoCtrl.NoAlt == Key.End;

    public string Input
    {
        get
        {
            // For special keys (Enter, Esc, etc.) AsRune returns 0.
            var rune = _key.AsRune;
            if (rune.Value == 0) return string.Empty;
            return ((char)rune.Value).ToString();
        }
    }

    /// <summary>
    /// Convenience method: resolve this key press through the KeybindingResolver
    /// and return the matched action (or null if no match).
    /// </summary>
    public string? ResolveAction(KeybindingResolver resolver, IReadOnlySet<string> activeContexts)
    {
        var result = resolver.Resolve(this, activeContexts);
        return result.Result == KeyResolveResult.Match ? result.Action : null;
    }
}
