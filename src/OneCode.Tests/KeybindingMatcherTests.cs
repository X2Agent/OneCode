using NSubstitute;
using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="KeybindingMatcher"/>
/// </summary>
public sealed class KeybindingMatcherTests
{
    [Fact]
    public void GetKeyName_EscapeKey_ReturnsEscape()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsEscape.Returns(true);

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("escape");
    }

    [Fact]
    public void GetKeyName_EnterKey_ReturnsEnter()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsReturn.Returns(true);

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("enter");
    }

    [Fact]
    public void GetKeyName_TabKey_ReturnsTab()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsTab.Returns(true);

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("tab");
    }

    [Fact]
    public void GetKeyName_BackspaceKey_ReturnsBackspace()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsBackspace.Returns(true);

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("backspace");
    }

    [Fact]
    public void GetKeyName_DeleteKey_ReturnsDelete()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsDelete.Returns(true);

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("delete");
    }

    [Fact]
    public void GetKeyName_ArrowKeys_ReturnsCorrectNames()
    {
        var keyInput = Substitute.For<IKeyInput>();

        keyInput.IsUpArrow.Returns(true);
        KeybindingMatcher.GetKeyName(keyInput).Should().Be("up");

        keyInput.IsUpArrow.Returns(false);
        keyInput.IsDownArrow.Returns(true);
        KeybindingMatcher.GetKeyName(keyInput).Should().Be("down");

        keyInput.IsDownArrow.Returns(false);
        keyInput.IsLeftArrow.Returns(true);
        KeybindingMatcher.GetKeyName(keyInput).Should().Be("left");

        keyInput.IsLeftArrow.Returns(false);
        keyInput.IsRightArrow.Returns(true);
        KeybindingMatcher.GetKeyName(keyInput).Should().Be("right");
    }

    [Fact]
    public void GetKeyName_SingleCharacterInput_ReturnsLowercase()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Input.Returns("A");

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().Be("a");
    }

    [Fact]
    public void GetKeyName_MultiCharacterInput_ReturnsNull()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Input.Returns("abc");

        var result = KeybindingMatcher.GetKeyName(keyInput);

        result.Should().BeNull();
    }

    [Fact]
    public void ModifiersMatch_SameModifiers_ReturnsTrue()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Meta.Returns(false);
        keyInput.Shift.Returns(true);
        keyInput.Super.Returns(false);

        var target = new ParsedKeystroke("a", true, false, true, false, false);

        var result = KeybindingMatcher.ModifiersMatch(keyInput, target);

        result.Should().BeTrue();
    }

    [Fact]
    public void ModifiersMatch_DifferentCtrl_ReturnsFalse()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);

        var target = new ParsedKeystroke("a", false, false, false, false, false);

        var result = KeybindingMatcher.ModifiersMatch(keyInput, target);

        result.Should().BeFalse();
    }

    [Fact]
    public void ModifiersMatch_AltAndMetaAreEquivalent_ReturnsTrue()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Meta.Returns(true);

        var target = new ParsedKeystroke("a", false, true, false, false, false);

        var result = KeybindingMatcher.ModifiersMatch(keyInput, target);

        result.Should().BeTrue();
    }

    [Fact]
    public void ModifiersMatch_MetaAndAltAreEquivalent_ReturnsTrue()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Meta.Returns(true);

        var target = new ParsedKeystroke("a", false, false, false, true, false);

        var result = KeybindingMatcher.ModifiersMatch(keyInput, target);

        result.Should().BeTrue();
    }

    [Fact]
    public void ModifiersMatch_DifferentSuper_ReturnsFalse()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Super.Returns(true);

        var target = new ParsedKeystroke("a", false, false, false, false, false);

        var result = KeybindingMatcher.ModifiersMatch(keyInput, target);

        result.Should().BeFalse();
    }

    [Fact]
    public void MatchesKeystroke_SameKeyAndModifiers_ReturnsTrue()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Input.Returns("a");

        var target = new ParsedKeystroke("a", true, false, false, false, false);

        var result = KeybindingMatcher.MatchesKeystroke(keyInput, target);

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesKeystroke_DifferentKey_ReturnsFalse()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Input.Returns("a");

        var target = new ParsedKeystroke("b", true, false, false, false, false);

        var result = KeybindingMatcher.MatchesKeystroke(keyInput, target);

        result.Should().BeFalse();
    }

    [Fact]
    public void MatchesKeystroke_EscapeKeyWithMeta_IgnoresMeta()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsEscape.Returns(true);
        keyInput.Meta.Returns(true);

        var target = new ParsedKeystroke("escape", false, false, false, false, false);

        var result = KeybindingMatcher.MatchesKeystroke(keyInput, target);

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesBinding_SingleKeyBinding_ReturnsTrue()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Input.Returns("a");

        var binding = new KeybindingEntry(
            "global",
            new[] { new ParsedKeystroke("a", true, false, false, false, false) },
            "test-action"
        );

        var result = KeybindingMatcher.MatchesBinding(keyInput, binding);

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesBinding_ChordBinding_ReturnsFalse()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Input.Returns("x");

        var binding = new KeybindingEntry(
            "global",
            new[]
            {
                new ParsedKeystroke("x", true, false, false, false, false),
                new ParsedKeystroke("c", true, false, false, false, false)
            },
            "test-action"
        );

        var result = KeybindingMatcher.MatchesBinding(keyInput, binding);

        result.Should().BeFalse();
    }

    [Fact]
    public void BuildKeystroke_SimpleKey_ReturnsParsedKeystroke()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Ctrl.Returns(true);
        keyInput.Input.Returns("a");

        var result = KeybindingMatcher.BuildKeystroke(keyInput);

        result.Should().NotBeNull();
        result!.Key.Should().Be("a");
        result.Ctrl.Should().BeTrue();
    }

    [Fact]
    public void BuildKeystroke_EscapeKey_ClearsMeta()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.IsEscape.Returns(true);
        keyInput.Meta.Returns(true);

        var result = KeybindingMatcher.BuildKeystroke(keyInput);

        result.Should().NotBeNull();
        result!.Key.Should().Be("escape");
        result.Alt.Should().BeFalse();
        result.Meta.Should().BeFalse();
    }

    [Fact]
    public void BuildKeystroke_NoKey_ReturnsNull()
    {
        var keyInput = Substitute.For<IKeyInput>();
        keyInput.Input.Returns("abc");

        var result = KeybindingMatcher.BuildKeystroke(keyInput);

        result.Should().BeNull();
    }

    [Fact]
    public void KeystrokesEqual_SameKeystrokes_ReturnsTrue()
    {
        var a = new ParsedKeystroke("a", true, false, true, false, false);
        var b = new ParsedKeystroke("a", true, false, true, false, false);

        var result = KeybindingMatcher.KeystrokesEqual(a, b);

        result.Should().BeTrue();
    }

    [Fact]
    public void KeystrokesEqual_AltAndMetaEquivalent_ReturnsTrue()
    {
        var a = new ParsedKeystroke("a", false, true, false, false, false);
        var b = new ParsedKeystroke("a", false, false, false, true, false);

        var result = KeybindingMatcher.KeystrokesEqual(a, b);

        result.Should().BeTrue();
    }

    [Fact]
    public void KeystrokesEqual_DifferentKey_ReturnsFalse()
    {
        var a = new ParsedKeystroke("a", true, false, false, false, false);
        var b = new ParsedKeystroke("b", true, false, false, false, false);

        var result = KeybindingMatcher.KeystrokesEqual(a, b);

        result.Should().BeFalse();
    }

    [Fact]
    public void KeystrokesEqual_DifferentModifiers_ReturnsFalse()
    {
        var a = new ParsedKeystroke("a", true, false, false, false, false);
        var b = new ParsedKeystroke("a", false, false, false, false, false);

        var result = KeybindingMatcher.KeystrokesEqual(a, b);

        result.Should().BeFalse();
    }

    [Fact]
    public void KeystrokesEqual_DifferentSuper_ReturnsFalse()
    {
        var a = new ParsedKeystroke("a", false, false, false, false, true);
        var b = new ParsedKeystroke("a", false, false, false, false, false);

        var result = KeybindingMatcher.KeystrokesEqual(a, b);

        result.Should().BeFalse();
    }
}
