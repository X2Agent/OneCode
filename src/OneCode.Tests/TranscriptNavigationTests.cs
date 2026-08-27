using NSubstitute;
using OneCode.App.Tui;
using OneCode.App.Transcript;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// Transcript 对话区导航（Ctrl+T 进入 / Esc·i 退出）守护：
/// - TranscriptViewModel 游标按「可交互行序数」锚定，索引漂移不丢位；
/// - ReplShell 进出模式切换上下文与高亮，j/k/Enter/C 经 Resolver 分发；
/// - 流式禁入：busy 开始时自动退出。
/// </summary>
public sealed class TranscriptNavigationTests
{
    // —— TranscriptViewModel（纯 C#）——

    [Fact]
    public void Move_OnEmptyList_ReturnsFalse()
    {
        var vm = new TranscriptViewModel(() => []);

        vm.Move(+1).Should().BeFalse();
        vm.CursorLine.Should().Be(-1);
    }

    [Fact]
    public void Move_ForwardFromUnset_EntersAtFirstAndSteps()
    {
        var vm = new TranscriptViewModel(() => [10, 20, 30]);

        vm.Move(+1).Should().BeTrue();
        vm.CursorLine.Should().Be(10);
        vm.Move(+1).Should().BeTrue();
        vm.CursorLine.Should().Be(20);
    }

    [Fact]
    public void Move_BackwardFromUnset_EntersAtLast()
    {
        var vm = new TranscriptViewModel(() => [10, 20, 30]);

        vm.Move(-1).Should().BeTrue();
        vm.CursorOrdinal.Should().Be(2);
    }

    [Fact]
    public void Move_AtEdge_ClampsWithoutMoving()
    {
        var vm = new TranscriptViewModel(() => [10, 20]);
        vm.MoveToEdge(first: false);

        vm.Move(+1).Should().BeFalse();
        vm.CursorOrdinal.Should().Be(1);

        vm.MoveToEdge(first: true);
        vm.Move(-1).Should().BeFalse();
        vm.CursorOrdinal.Should().Be(0);
    }

    [Fact]
    public void Refresh_ListShrinks_ClampsOrdinal()
    {
        var lines = new List<int> { 5, 10, 15 };
        var vm = new TranscriptViewModel(() => lines)
        {
            // 定位到最后一项
        };
        vm.MoveToEdge(first: false);
        vm.CursorOrdinal.Should().Be(2);

        lines.RemoveRange(1, 2); // 折叠/清除后只剩 1 个可交互行
        vm.Refresh();

        vm.CursorOrdinal.Should().Be(0);
        vm.CursorLine.Should().Be(5);
    }

    [Fact]
    public void CursorLine_IndexDrift_OrdinalAnchorKeepsPosition()
    {
        // 模拟真实提供者：仅返回可交互行（普通文本行不进入快照）。
        var interactive = new List<int> { 10, 20 };
        var vm = new TranscriptViewModel(() => interactive);
        vm.Move(+1); // 序数 0 → 行 10

        // 上方插入三行普通文本：可交互行序数不变，绝对行号整体下移。
        interactive[0] = 13;
        interactive[1] = 23;
        vm.Refresh();

        vm.CursorOrdinal.Should().Be(0, "序数锚定不受上方插入影响");
        vm.CursorLine.Should().Be(13, "绝对行号跟随新快照");
    }

    // —— ReplShell 集成 ——

    [Fact]
    public void EnterTranscriptMode_ActivatesAndHighlightsOnJ()
    {
        var shell = CreateShellWithToolLines(out var firstToolLine, out _);

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.T.WithCtrl);
        shell.IsTranscriptNavActive.Should().BeTrue("Ctrl+T 进入导航模式");

        shell.DispatchShellKey(Terminal.Gui.Input.Key.J);
        shell.Transcript.MessageView.NavigationHighlightLine.Should().Be(firstToolLine,
            "首个 j 从最近端落到第一个可交互行");
    }

    [Fact]
    public void TranscriptNav_ToggleExpands_AndKeepsHighlightOnBlockLine()
    {
        var shell = CreateShellWithToolLines(out _, out _);
        shell.EnterTranscriptMode();

        shell.DispatchShellKey(Terminal.Gui.Input.Key.Enter);

        var view = shell.Transcript.MessageView;
        view.TotalLines.Should().BeGreaterThan(2, "展开插入了工具详情行");
        view.NavigationHighlightLine.Should().BeGreaterThanOrEqualTo(0);
        view.GetInteractiveLineIndices()[0].Should().Be(view.NavigationHighlightLine,
            "展开后序数锚点仍指向同一可交互块");
    }

    [Fact]
    public void TranscriptNav_Esc_ExitsAndRestoresFocusPath()
    {
        var shell = CreateShellWithToolLines(out _, out _);
        shell.EnterTranscriptMode();

        shell.DispatchShellKey(Terminal.Gui.Input.Key.Esc);

        shell.IsTranscriptNavActive.Should().BeFalse();
        shell.Transcript.MessageView.NavigationHighlightLine.Should().Be(-1);
    }

    [Fact]
    public void TranscriptNav_BusyStart_AutoExits()
    {
        var shell = CreateShellWithToolLines(out _, out _);
        shell.EnterTranscriptMode();
        shell.IsTranscriptNavActive.Should().BeTrue();

        shell.SetAgentBusy(true);

        shell.IsTranscriptNavActive.Should().BeFalse("流式禁入：busy 开始即退出导航");
    }

    [Fact]
    public void EnterTranscriptMode_WhileBusy_IsBlocked()
    {
        var shell = CreateShellWithToolLines(out _, out _);
        shell.SetAgentBusy(true);

        shell.EnterTranscriptMode();

        shell.IsTranscriptNavActive.Should().BeFalse();
    }

    private static ReplShell CreateShellWithToolLines(out int firstToolLine, out int secondToolLine)
    {
        var shell = CreateShell();
        var view = shell.Transcript.MessageView;
        view.AppendLines(
        [
            FormattedLine.Plain("普通文本行", TuiPalette.FgPrimary),
            FormattedLine.PlainWithTag("⏺ Tool(Bash)", TuiPalette.FgSecondary,
                new ToolLineTag("Bash", "git status", null, IsExpanded: false)),
            FormattedLine.Plain("中间文本行", TuiPalette.FgPrimary),
            FormattedLine.PlainWithTag("⚙ 思考", TuiPalette.FgMuted,
                new ThinkingLineTag("分析中…", IsExpanded: false)),
        ]);
        firstToolLine = 1;
        secondToolLine = 3;
        return shell;
    }

    private static ReplShell CreateShell()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);

        var resolver = new OneCode.Core.Keybindings.KeybindingResolver();
        resolver.SetBindings([.. OneCode.Core.Keybindings.KeybindingDefaults.GetDefaultParsedBindings()]);
        var keyContextManager = new OneCode.Core.Keybindings.KeybindingContextManager
        {
            FocusContext = OneCode.Core.Keybindings.KeybindingDefaults.ContextChat,
        };

        return new ReplShell(
            app,
            version: "test",
            model: "test-model",
            sshHost: null,
            slashCommands: [],
            modeController: new WorkingModeController(),
            keyResolver: resolver,
            keyContextManager: keyContextManager,
            clipboard: null,
            historyProvider: null,
            toolNameProvider: () => []);
    }
}
