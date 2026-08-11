using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using Terminal.Gui.Input;

namespace OneCode.Tests;

public sealed class TuiKeyAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveAction_ShiftArrow_MapsToConversationScroll(bool scrollUp)
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var contexts = new HashSet<string>
        {
            KeybindingDefaults.ContextGlobal,
            KeybindingDefaults.ContextChat,
        };
        var key = scrollUp ? Key.CursorUp.WithShift : Key.CursorDown.WithShift;
        var adapter = new TuiKeyAdapter(key);

        var action = adapter.ResolveAction(resolver, contexts);

        action.Should().Be(scrollUp
            ? KeybindingDefaults.ActionChatScrollUp
            : KeybindingDefaults.ActionChatScrollDown);
    }
}
