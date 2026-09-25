using NSubstitute;

using OneCode.App.Tui;
using OneCode.Core.Commands;

using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OneCode.Tests;

/// <summary>
/// <c>/diff</c> 变更审查弹层的层级契约（keybindings 计划 §6#7）：
/// 裸命令弹层 → 列表选文件进第二层 → Esc 逐层退回。
///
/// 断言面是 <see cref="OverlayHost"/> 的真实栈深与弹层 <see cref="ListView"/> 的真实数据源，
/// 不是构造参数：层级错乱（一次关到底 / 进不了第二层）必须让这些用例变红。
/// </summary>
public sealed class OneCodeToplevelDiffOverlayTests
{
    private const string FilePath = "src/OneCode.App/Tui/OneCodeToplevel.cs";
    private const string DiffText = "@@ -1,2 +1,2 @@\n-旧行\n+新行";

    [Fact]
    public async Task Submit_BareDiff_OpensReviewOverlayListingChangedFiles()
    {
        var git = Substitute.For<IGitHelper>();
        git.GetPendingDiffStatAsync(Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns([new ReviewFileEntry(FilePath, 3, 1, "M")]);

        using var shell = TuiTestShell.Create(b => b.Immediate("diff").GitHelper(git));

        shell.Submit("/diff");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/diff 推入审查弹层");

        shell.Overlays.IsOverlayVisible.Should().BeTrue();
        shell.Overlays.Top.Should().BeOfType<ReviewOverlay>();
        shell.TranscriptText.Should().Contain("打开变更审查", "弹层与一行提示必须同时出现，否则用户看不到反馈");

        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().ContainSingle()
            .Which.Should().Contain(FilePath).And.Contain("+3").And.Contain("-1");
    }

    [Fact]
    public async Task DiffOverlay_EnterOnFile_OpensDiffDetailAsSecondLayer()
    {
        var git = Substitute.For<IGitHelper>();
        git.GetPendingDiffStatAsync(Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns([new ReviewFileEntry(FilePath, 3, 1, "M")]);
        git.GetFileDiffAgainstHeadAsync(FilePath, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(DiffText);

        using var shell = TuiTestShell.Create(b => b.Immediate("diff").GitHelper(git));

        shell.Submit("/diff");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/diff 推入审查弹层");

        var review = shell.Overlays.Top!;
        var list = review.SubViews.OfType<ListView>().Single();
        list.SelectedItem.Should().Be(0, "非空列表默认选中首项，Enter 才有确定的目标文件");

        list.NewKeyDownEvent(Key.Enter);
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 2, "Enter 在审查列表上打开差异详情");

        shell.Overlays.Top.Should().BeOfType<DiffDetailOverlay>();
        ((CenteredOverlay)shell.Overlays.Top!).Title.Should().Contain(
            FilePath, "详情层必须绑定被选中的文件，选错行的回归在这里暴露");
        await git.Received(1).GetFileDiffAgainstHeadAsync(FilePath, Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task DiffDetailOverlay_Esc_ReturnsToReviewListInsteadOfClosingEverything()
    {
        var git = Substitute.For<IGitHelper>();
        git.GetPendingDiffStatAsync(Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns([new ReviewFileEntry(FilePath, 3, 1, "M")]);
        git.GetFileDiffAgainstHeadAsync(FilePath, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(DiffText);

        using var shell = TuiTestShell.Create(b => b.Immediate("diff").GitHelper(git));

        shell.Submit("/diff");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/diff 推入审查弹层");
        var review = shell.Overlays.Top!;
        review.SubViews.OfType<ListView>().Single().NewKeyDownEvent(Key.Enter);
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 2, "Enter 打开差异详情");

        shell.DispatchKeyDown(Key.Esc).Should().BeTrue("Esc 落在弹层拦截链上");

        shell.Overlays.Depth.Should().Be(1, "Esc 只退一层：详情 → 列表");
        shell.Overlays.Top.Should().BeSameAs(review);
        shell.Overlays.IsOverlayVisible.Should().BeTrue();

        shell.DispatchKeyDown(Key.Esc).Should().BeTrue();
        shell.Overlays.Depth.Should().Be(0, "列表层再按 Esc 才彻底关闭");
        shell.Overlays.IsOverlayVisible.Should().BeFalse();
    }

    [Fact]
    public void EscWithoutOverlay_IsNotConsumedByTheOverlayChain()
    {
        using var shell = TuiTestShell.Create();

        shell.DispatchKeyDown(Key.Esc).Should().BeFalse(
            "无弹层时 Esc 不得被弹层链吞掉 —— 否则上一条用例的 BeTrue 只是恒真");
        shell.Overlays.Depth.Should().Be(0);
    }

    [Fact]
    public async Task Submit_DiffWithArguments_RunsTextPathWithoutOpeningOverlay()
    {
        var executed = new List<string>();
        var git = Substitute.For<IGitHelper>();
        using var shell = TuiTestShell.Create(b => b
            .Immediate("diff")
            .GitHelper(git)
            .OnCommand(text =>
            {
                executed.Add(text);
                return "文本输出标记";
            }));

        await shell.SubmitCommandAsync("/diff --staged");

        executed.Should().ContainSingle().Which.Should().Be("/diff --staged");
        shell.Overlays.Depth.Should().Be(0, "带参数的 /diff 走文本输出，不得抢走成弹层");
        shell.TranscriptText.Should().Contain("文本输出标记");
        await git.DidNotReceive().GetPendingDiffStatAsync(Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Submit_BareDiff_WithoutGitHelper_ShowsEmptyState()
    {
        using var shell = TuiTestShell.Create(b => b.Immediate("diff"));

        shell.Submit("/diff");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/diff 推入审查弹层");

        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().Contain("（未检测到 Git 变更文件）", "无 Git 服务时必须给出可读空态而不是空白弹层");
        rows.Should().NotContain(row => row.Contains("📄", StringComparison.Ordinal),
            "空态与有条目态必须是两条不同的渲染分支");
    }

    [Fact]
    public async Task Submit_BareDiff_GitReportsNoChanges_ShowsEmptyState()
    {
        var git = Substitute.For<IGitHelper>();
        git.GetPendingDiffStatAsync(Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns([]);
        using var shell = TuiTestShell.Create(b => b.Immediate("diff").GitHelper(git));

        shell.Submit("/diff");
        await shell.WaitUntilAsync(() => shell.Overlays.Depth == 1, "/diff 推入审查弹层");

        var rows = TuiTestShell.ListRowsOf(shell.Overlays.Top!);
        rows.Should().Contain("（未检测到 Git 变更文件）");
        rows.Should().NotContain(row => row.Contains("📄", StringComparison.Ordinal));
    }
}
