using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="KeybindingParser"/>
/// </summary>
public sealed class KeybindingParserTests
{
    [Fact]
    public void ParseKeystroke_SingleKey_ReturnsParsedKeystroke()
    {
        var result = KeybindingParser.ParseKeystroke("a");

        result.Key.Should().Be("a");
        result.Ctrl.Should().BeFalse();
        result.Alt.Should().BeFalse();
        result.Shift.Should().BeFalse();
    }

    [Fact]
    public void ParseKeystroke_CtrlModifier_SetsCtrlFlag()
    {
        var result = KeybindingParser.ParseKeystroke("ctrl+a");

        result.Key.Should().Be("a");
        result.Ctrl.Should().BeTrue();
        result.Alt.Should().BeFalse();
    }

    [Fact]
    public void ParseKeystroke_AltModifier_SetsAltFlag()
    {
        var result = KeybindingParser.ParseKeystroke("alt+a");

        result.Key.Should().Be("a");
        result.Alt.Should().BeTrue();
        result.Ctrl.Should().BeFalse();
    }

    [Fact]
    public void ParseKeystroke_ShiftModifier_SetsShiftFlag()
    {
        var result = KeybindingParser.ParseKeystroke("shift+a");

        result.Key.Should().Be("a");
        result.Shift.Should().BeTrue();
    }

    [Fact]
    public void ParseKeystroke_MultipleModifiers_SetsAllFlags()
    {
        var result = KeybindingParser.ParseKeystroke("ctrl+shift+alt+a");

        result.Key.Should().Be("a");
        result.Ctrl.Should().BeTrue();
        result.Alt.Should().BeTrue();
        result.Shift.Should().BeTrue();
    }

    [Theory]
    [InlineData("escape", "escape")]
    [InlineData("esc", "escape")]
    [InlineData("enter", "enter")]
    [InlineData("return", "enter")]
    [InlineData("space", " ")]
    [InlineData("tab", "tab")]
    [InlineData("backspace", "backspace")]
    [InlineData("delete", "delete")]
    [InlineData("del", "delete")]
    [InlineData("up", "up")]
    [InlineData("down", "down")]
    [InlineData("left", "left")]
    [InlineData("right", "right")]
    [InlineData("pageup", "pageup")]
    [InlineData("pgup", "pageup")]
    [InlineData("pagedown", "pagedown")]
    [InlineData("pgdn", "pagedown")]
    [InlineData("home", "home")]
    [InlineData("end", "end")]
    [InlineData("insert", "insert")]
    [InlineData("ins", "insert")]
    public void ParseKeystroke_SpecialKeys_NormalizesKeyName(string input, string expectedKey)
    {
        var result = KeybindingParser.ParseKeystroke(input);

        result.Key.Should().Be(expectedKey);
    }

    [Fact]
    public void ParseKeystroke_CaseInsensitive_ParsesCorrectly()
    {
        var result = KeybindingParser.ParseKeystroke("CTRL+SHIFT+A");

        result.Key.Should().Be("a");
        result.Ctrl.Should().BeTrue();
        result.Shift.Should().BeTrue();
    }

    [Fact]
    public void ParseKeystroke_ControlAlias_SetsCtrlFlag()
    {
        var result = KeybindingParser.ParseKeystroke("control+a");

        result.Ctrl.Should().BeTrue();
    }

    [Fact]
    public void ParseKeystroke_OptAlias_SetsAltFlag()
    {
        var result = KeybindingParser.ParseKeystroke("opt+a");

        result.Alt.Should().BeTrue();
    }

    [Fact]
    public void ParseKeystroke_CmdAlias_SetsSuperFlag()
    {
        var result = KeybindingParser.ParseKeystroke("cmd+a");

        result.Super.Should().BeTrue();
    }

    [Fact]
    public void ParseChord_SingleKeystroke_ReturnsArrayWithOneElement()
    {
        var result = KeybindingParser.ParseChord("ctrl+a");

        result.Should().HaveCount(1);
        result[0].Key.Should().Be("a");
        result[0].Ctrl.Should().BeTrue();
    }

    [Fact]
    public void ParseChord_MultipleKeystrokes_ReturnsArrayWithAllElements()
    {
        var result = KeybindingParser.ParseChord("ctrl+x ctrl+k");

        result.Should().HaveCount(2);
        result[0].Key.Should().Be("x");
        result[0].Ctrl.Should().BeTrue();
        result[1].Key.Should().Be("k");
        result[1].Ctrl.Should().BeTrue();
    }

    [Fact]
    public void ParseChord_SpaceKey_ReturnsSingleSpaceKeystroke()
    {
        var result = KeybindingParser.ParseChord(" ");

        result.Should().HaveCount(1);
        result[0].Key.Should().Be(" ");
    }

    [Fact]
    public void KeystrokeToString_SimpleKey_ReturnsKeyName()
    {
        var keystroke = new ParsedKeystroke("a", false, false, false, false, false);

        var result = KeybindingParser.KeystrokeToString(keystroke);

        result.Should().Be("a");
    }

    [Fact]
    public void KeystrokeToString_WithModifiers_ReturnsFormattedString()
    {
        var keystroke = new ParsedKeystroke("a", true, false, true, false, false);

        var result = KeybindingParser.KeystrokeToString(keystroke);

        result.Should().Be("ctrl+shift+a");
    }

    [Fact]
    public void KeystrokeToString_SpecialKey_ReturnsDisplayName()
    {
        var keystroke = new ParsedKeystroke("escape", false, false, false, false, false);

        var result = KeybindingParser.KeystrokeToString(keystroke);

        result.Should().Be("Esc");
    }

    [Fact]
    public void ChordToString_MultipleKeystrokes_ReturnsSpaceSeparatedString()
    {
        var chord = new[]
        {
            new ParsedKeystroke("x", true, false, false, false, false),
            new ParsedKeystroke("k", true, false, false, false, false)
        };

        var result = KeybindingParser.ChordToString(chord);

        result.Should().Be("ctrl+x ctrl+k");
    }

    [Fact]
    public void KeystrokeToDisplayString_MacOSPlatform_UsesOptAndCmd()
    {
        var keystroke = new ParsedKeystroke("a", false, true, false, false, true);

        var result = KeybindingParser.KeystrokeToDisplayString(keystroke, DisplayPlatform.MacOS);

        result.Should().Contain("opt");
        result.Should().Contain("cmd");
    }

    [Fact]
    public void KeystrokeToDisplayString_LinuxPlatform_UsesAltAndSuper()
    {
        var keystroke = new ParsedKeystroke("a", false, true, false, false, true);

        var result = KeybindingParser.KeystrokeToDisplayString(keystroke, DisplayPlatform.Linux);

        result.Should().Contain("alt");
        result.Should().Contain("super");
    }

    [Fact]
    public void ParseBindings_MultipleBlocks_ReturnsFlattenedList()
    {
        var blocks = new[]
        {
            new KeybindingBlock("global", new Dictionary<string, string?>
            {
                ["ctrl+a"] = "action1",
                ["ctrl+b"] = "action2"
            }),
            new KeybindingBlock("editor", new Dictionary<string, string?>
            {
                ["ctrl+c"] = "action3"
            })
        };

        var result = KeybindingParser.ParseBindings(blocks);

        result.Should().HaveCount(3);
        result[0].Context.Should().Be("global");
        result[0].Action.Should().Be("action1");
        result[2].Context.Should().Be("editor");
        result[2].Action.Should().Be("action3");
    }

    [Fact]
    public void NormalizeKeyForComparison_DifferentOrder_ReturnsSameString()
    {
        var result1 = KeybindingParser.NormalizeKeyForComparison("ctrl+shift+a");
        var result2 = KeybindingParser.NormalizeKeyForComparison("shift+ctrl+a");

        result1.Should().Be(result2);
    }

    [Fact]
    public void NormalizeKeyForComparison_Aliases_ReturnsSameString()
    {
        var result1 = KeybindingParser.NormalizeKeyForComparison("alt+a");
        var result2 = KeybindingParser.NormalizeKeyForComparison("opt+a");

        result1.Should().Be(result2);
    }

    [Fact]
    public void NormalizeKeyForComparison_ChordSequence_NormalizesEachStep()
    {
        var result = KeybindingParser.NormalizeKeyForComparison("ctrl+x ctrl+k");

        result.Should().Contain("ctrl+x");
        result.Should().Contain("ctrl+k");
    }
}
