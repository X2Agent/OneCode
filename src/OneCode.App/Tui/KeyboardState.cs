using System.Runtime.InteropServices;

namespace OneCode.App.Tui;

/// <summary>
/// OS-level keyboard state queries, used as a fallback when Terminal.Gui
/// cannot detect modifier keys.
///
/// On Windows under ConPTY (Windows Terminal, VS Code terminal) without the
/// kitty keyboard protocol, Shift+Enter arrives as a bare \r — Terminal.Gui
/// decodes it as <c>Key.Enter</c> without the Shift flag. This helper queries
/// the physical keyboard state directly via <c>GetAsyncKeyState</c> to recover
/// the Shift modifier.
/// </summary>
internal static class KeyboardState
{
    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Returns true if the Shift key is currently physically pressed.
    /// Always returns false on non-Windows platforms (kitty protocol is
    /// expected to handle modifier detection there).
    /// </summary>
    public static bool IsShiftPressed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
    }
}
