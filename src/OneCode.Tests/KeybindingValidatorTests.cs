using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// <see cref="KeybindingValidator"/> 的对外契约：<c>/keybindings list</c> 弹层的
/// 「配置警告」区与 <c>KeybindingsCommand</c> 的文本输出都消费它的结果。
///
/// 校验器此前零覆盖，且它只面向**用户配置块**（默认绑定含 <c>ctrl+d</c> 这类保留键，
/// 是硬编码行为，不属于用户可改范围），因此用例只喂用户块，不喂
/// <see cref="KeybindingDefaults.DefaultBindings"/>。
/// </summary>
public sealed class KeybindingValidatorTests
{
    [Fact]
    public void ValidateBindings_UserRebindsReservedCtrlD_ReportsErrorForThatKey()
    {
        var blocks = new List<KeybindingBlock>
        {
            new(KeybindingDefaults.ContextGlobal, new Dictionary<string, string?>
            {
                ["ctrl+d"] = KeybindingDefaults.ActionAppSidebarToggle,
            }),
        };

        var warnings = KeybindingValidator.ValidateBindings(blocks);

        var warning = warnings.Should().ContainSingle().Subject;
        warning.Type.Should().Be(KeybindingWarningType.Reserved);
        warning.Severity.Should().Be(KeybindingSeverity.Error,
            "ctrl+d 是硬编码退出键，被用户覆盖后必须报错而不是静默失效");
        warning.Key.Should().Be("ctrl+d");
    }

    [Fact]
    public void ValidateBindings_UserBindsFreeKeyToKnownAction_ReportsNothing()
    {
        var blocks = new List<KeybindingBlock>
        {
            new(KeybindingDefaults.ContextChat, new Dictionary<string, string?>
            {
                ["ctrl+k"] = KeybindingDefaults.ActionChatScrollUp,
            }),
        };

        KeybindingValidator.ValidateBindings(blocks).Should().BeEmpty(
            "反证：合法用户绑定不得产生噪声警告，否则上一条的「有警告」不构成信号");
    }

    [Fact]
    public void ValidateBindings_SameKeyReboundInTwoBlocks_ReportsDuplicate()
    {
        var blocks = new List<KeybindingBlock>
        {
            new(KeybindingDefaults.ContextChat, new Dictionary<string, string?>
            {
                ["ctrl+k"] = KeybindingDefaults.ActionChatScrollUp,
            }),
            new(KeybindingDefaults.ContextChat, new Dictionary<string, string?>
            {
                ["ctrl+k"] = KeybindingDefaults.ActionChatScrollDown,
            }),
        };

        var warnings = KeybindingValidator.ValidateBindings(blocks);

        var warning = warnings.Should().ContainSingle().Subject;
        warning.Type.Should().Be(KeybindingWarningType.Duplicate,
            "同一上下文重复绑定只留最后一条，用户必须被告知前面的绑定已失效");
        warning.Key.Should().Be("ctrl+k");
        warning.Context.Should().Be(KeybindingDefaults.ContextChat);
    }
}
