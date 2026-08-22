using OneCode.App.Tui;
using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="KeybindingsOverlay"/>：overlay 行格式化的真实产出。
/// </summary>
public sealed class KeybindingsOverlayTests
{
    [Fact]
    public void FormatRows_GroupsByContextWithSectionHeaders()
    {
        var views = new List<KeybindingView>
        {
            new("Global", "Ctrl+D", "app:exit", KeybindingSource.Default),
            new("Chat", "Enter", "chat:submit", KeybindingSource.Default),
        };

        var rows = KeybindingsOverlay.FormatRows(views, []);

        rows.Should().Contain("— Global —");
        rows.Should().Contain("— Chat —");
        rows.Should().Contain(r => r.Contains("Ctrl+D") && r.Contains("app:exit"));
    }

    [Fact]
    public void FormatRows_CustomBinding_ShowsCustomMark()
    {
        var views = new List<KeybindingView>
        {
            new("Chat", "Ctrl+G", "command:foo", KeybindingSource.Custom),
        };

        var rows = KeybindingsOverlay.FormatRows(views, []);

        rows.Should().Contain(r => r.Contains("Ctrl+G") && r.Contains("command:foo") && r.Contains("★自定义"));
    }

    [Fact]
    public void FormatRows_UnboundBinding_ShowsUnboundText()
    {
        var views = new List<KeybindingView>
        {
            new("Chat", "Ctrl+G", null, KeybindingSource.Unbound),
        };

        var rows = KeybindingsOverlay.FormatRows(views, []);

        rows.Should().Contain(r => r.Contains("Ctrl+G") && r.Contains("(已解绑)"));
    }

    [Fact]
    public void FormatRows_WithWarnings_ShowsWarningSection()
    {
        var warning = new KeybindingWarning(
            KeybindingWarningType.Duplicate,
            KeybindingSeverity.Warning,
            "Duplicate binding \"ctrl+g\" in Chat context",
            Key: "ctrl+g",
            Context: "Chat");

        var rows = KeybindingsOverlay.FormatRows([], [warning]);

        rows.Should().Contain(r => r.Contains("配置警告"));
        rows.Should().Contain(r => r.Contains("Duplicate binding \"ctrl+g\" in Chat context"));
    }

    [Fact]
    public void FormatRows_NoWarnings_OmitsWarningSection()
    {
        var rows = KeybindingsOverlay.FormatRows([], []);

        rows.Should().NotContain(r => r.Contains("配置警告"));
    }
}
