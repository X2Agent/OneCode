using OneCode.App.Tui;
using OneCode.Core.Keybindings;

using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// <c>/keybindings list</c> 快捷键查看弹层的端到端接线（keybindings 计划 §6#9）：
/// 命令输入 → 立即执行通道 → overlay 压栈 → 真实生效绑定与校验警告落屏 → Esc 关闭。
///
/// 行格式化本身已有 <see cref="KeybindingsOverlayTests"/> 覆盖；本类只断言
/// 「这条链路是否真的存在」——尤其是警告是否来自运行期服务而不是写死的空列表。
/// </summary>
public sealed class OneCodeToplevelKeybindingsOverlayTests
{
    [Fact]
    public async Task Submit_BareKeybindings_OpensOverlayListingEffectiveBindings()
    {
        using var shell = TuiTestShell.Create(b => b.Immediate("keybindings"));

        shell.Submit("/keybindings");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/keybindings 推入快捷键弹层");

        shell.Overlays.IsOverlayVisible.Should().BeTrue();
        shell.Overlays.Top.Should().BeOfType<KeybindingsOverlay>();

        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().Contain("— Global —", "绑定按 Context 分组，缺组头说明展示的是空列表");
        rows.Should().Contain(row => row.Contains("Ctrl+D") && row.Contains("app:exit"),
            "默认解析器的绑定必须真的流进弹层，否则用户看到的是一份空表");
        rows.Should().Contain(row => row.Contains("硬编码"),
            "不可重映射的硬编码键区必须一并展示，否则用户会误以为能改");
    }

    [Fact]
    public async Task Submit_KeybindingsListAlias_OpensSameOverlay()
    {
        using var shell = TuiTestShell.Create(b => b.Immediate("keybindings"));

        shell.Submit("/keybindings list");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/keybindings list 推入快捷键弹层");

        shell.Overlays.Top.Should().BeOfType<KeybindingsOverlay>();
        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().Contain(row => row.Contains("Ctrl+D") && row.Contains("app:exit"));
        rows.Should().NotContain(row => row.Contains("配置警告"),
            "配置干净时不得出现警告区 —— 否则下面那条注入用例的断言就没有区分度");
    }

    [Fact]
    public async Task Submit_Keybindings_WithLoaderWarnings_ShowsWarningSectionFromRuntimeService()
    {
        var warnings = new List<KeybindingWarning>
        {
            new(
                KeybindingWarningType.Duplicate,
                KeybindingSeverity.Error,
                "重复绑定：ctrl+g",
                Key: "ctrl+g",
                Context: KeybindingDefaults.ContextGlobal,
                Action: KeybindingDefaults.ActionAppSidebarToggle),
        };

        using var shell = TuiTestShell.Create(b => b
            .Immediate("keybindings")
            .KeybindingWarnings([.. warnings]));

        shell.Submit("/keybindings list");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/keybindings list 推入快捷键弹层");

        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().Contain("— 配置警告 (1) —");
        rows.Should().Contain("  ✗ 重复绑定：ctrl+g", "Error 级警告用 ✗，用户必须能一眼区分严重程度");
    }

    [Fact]
    public async Task Esc_OnKeybindingsOverlay_ClosesItAndIsConsumed()
    {
        using var shell = TuiTestShell.Create(b => b.Immediate("keybindings"));

        shell.Submit("/keybindings");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/keybindings 推入快捷键弹层");

        shell.Shell.DispatchKeyDown(Key.Esc).Should().BeTrue("Esc 被弹层链消费");

        shell.Overlays.Depth.Should().Be(0);
        shell.Overlays.IsOverlayVisible.Should().BeFalse();
        shell.Shell.DispatchKeyDown(Key.Esc).Should().BeFalse(
            "关闭后 Esc 不再被弹层链吞掉 —— 上一条 BeTrue 才有意义");
    }

    [Fact]
    public async Task Submit_KeybindingsOpen_RunsTextPathWithoutOpeningOverlay()
    {
        var executed = new List<string>();
        using var shell = TuiTestShell.Create(b => b
            .Immediate("keybindings")
            .OnCommand(text =>
            {
                executed.Add(text);
                return "编辑绑定提示";
            }));

        await shell.SubmitCommandAsync("/keybindings open");

        executed.Should().ContainSingle().Which.Should().Be("/keybindings open");
        shell.Overlays.Depth.Should().Be(0, "open/reset 属编辑类子命令，不得被查看弹层截走");
        shell.TranscriptText.Should().Contain("编辑绑定提示");
    }
}
