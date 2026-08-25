using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// 平台保留快捷键行为守护：
/// - NonRebindable（ctrl+d / ctrl+m）在所有平台恒定保留；
/// - TerminalReserved（SIGTSTP/SIGQUIT）仅适用于 Unix——Windows 控制台不发送这些信号，
///   键位可正常绑定，因此 GetReservedShortcuts 在 Windows 上不得包含它们。
/// 该分支曾为全平台生效，回归锚定防止无意改回。
/// </summary>
public sealed class KeybindingDefaultsTests
{
    [Fact]
    public void GetReservedShortcuts_AlwaysContainsNonRebindable()
    {
        var reserved = KeybindingDefaults.GetReservedShortcuts();
        reserved.Should().Contain(r => r.Key == "ctrl+d");
        reserved.Should().Contain(r => r.Key == "ctrl+m");
    }

    [Fact]
    public void GetReservedShortcuts_TerminalSignalsOnlyOnUnix()
    {
        var shortcuts = KeybindingDefaults.GetReservedShortcuts()
            .Select(r => r.Key).ToList();

        if (OperatingSystem.IsWindows())
        {
            // Windows 控制台无 SIGTSTP/SIGQUIT：ctrl+z、ctrl+\ 必须可绑定。
            shortcuts.Should().NotContain("ctrl+z");
            shortcuts.Should().NotContain("ctrl+\\");
        }
        else
        {
            shortcuts.Should().Contain("ctrl+z");
            shortcuts.Should().Contain("ctrl+\\");
        }
    }

    [Fact]
    public void AllActions_StrategyToggleRemoved_CommandPaletteRemoved()
    {
        // 编排模式由 team.yaml 固定声明：策略切换键已移除；
        // 命令面板动作已移除。防止从默认绑定表"复活"。
        KeybindingDefaults.AllActions.Should().NotContain("chat:toggleStrategy");
        KeybindingDefaults.AllActions.Should().NotContain("app:commandPalette");
        KeybindingDefaults.AllActions.Should().Contain("chat:cycleTeam");
    }
}