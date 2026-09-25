using System.Runtime.CompilerServices;

using NSubstitute;

using OneCode.App.Tui;

using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// 输入闭环的交互契约（keybindings 计划 §6#1）：回车提交 → 查询流事件 → 会话区渲染，
/// 外加退出与打断两条离开该闭环的分支。
///
/// 被测对象是 <see cref="OneCodeToplevel"/> 的真实提交管线（OnUserSubmitted →
/// HandleSubmitCoreAsync / RunQueryAsync → DispatchEvent），不是替身行为。
/// </summary>
public sealed class OneCodeToplevelSubmitLoopTests
{
    private const string UserPrompt = "解释一下这个项目的结构";

    [Fact]
    public async Task Submit_StreamedReply_RendersUserPromptAndAssistantText()
    {
        using var shell = TuiTestShell.Create(b => b.Stream(
            new TuiTextDelta("流式回复甲"),
            new TuiTextDelta("流式回复乙"),
            new TuiDone(10, 20)));

        await shell.SubmitQueryAsync(UserPrompt);

        var lines = shell.RenderedLines;
        lines.Should().ContainSingle(
            line => line.Contains(UserPrompt, StringComparison.Ordinal),
            "用户气泡必须恰好出现一次，重复渲染是会直接看到的回归");
        shell.TranscriptText.Should().Contain("流式回复甲", "流式增量必须落进会话区");
        shell.TranscriptText.Should().Contain("流式回复乙");
        shell.Transcript.MessageView.IsStreaming.Should().BeFalse("一轮结束后流式预览必须收尾");
    }

    [Fact]
    public async Task Submit_ToolCallCycle_RendersToolNameAndResult()
    {
        using var shell = TuiTestShell.Create(b => b.Stream(
            new TuiToolStart("t-1", "read_file"),
            new TuiToolDone("read_file", IsError: false, Result: "工具输出标记", ToolId: "t-1"),
            new TuiDone(10, 20)));

        await shell.SubmitQueryAsync("读一下配置");

        var toolLines = shell.RenderedLines
            .Where(line => line.Contains("read_file", StringComparison.Ordinal))
            .ToList();
        toolLines.Should().ContainSingle(
            "工具的开始与结束必须配对成同一行（ToolTracker 去重契约），多出一行就是重复渲染");
        toolLines[0].Should().Contain(
            "· (", "结束事件要把启动行改写成完成态，而不是再追加一行");
    }

    [Fact]
    public async Task Submit_QueryFails_ErrorTextReachesTranscript()
    {
        using var shell = TuiTestShell.Create(b => b.Stream(new TuiError("查询失败标记")));

        await shell.SubmitQueryAsync("随便问问");

        shell.TranscriptText.Should().Contain("查询失败标记");
        shell.Transcript.MessageView.IsStreaming.Should().BeFalse("异常路径同样必须收尾，否则输入框永久 busy");
    }

    [Fact]
    public void SlashQuit_RequestsStopWithZeroExitCode()
    {
        using var shell = TuiTestShell.Create();

        shell.Submit("/quit");

        shell.Toplevel.ExitCode.Should().Be(0, "主动退出是正常终止");
        // Runnable.RequestStop() 转发的是 App.RequestStop(this)（IRunnable 重载），
        // 不是无参的 RequestStop()——断言哪个重载就是断言宿主收到的是哪条停止指令。
        shell.App.Received(1).RequestStop(shell.Toplevel);
    }

    [Fact]
    public void CtrlD_OnFocusedInput_RequestsStopWithZeroExitCode()
    {
        using var shell = TuiTestShell.Create();

        shell.TypeKey(Key.D.WithCtrl);

        shell.Toplevel.ExitCode.Should().Be(0);
        shell.App.Received(1).RequestStop(shell.Toplevel);
    }

    [Fact]
    public async Task EscWhileQueryRunning_CancelsWithoutExiting()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = TuiTestShell.Create(
            b => b.StreamQuery((_, _, ct) => Blocked(streamEntered, gate.Task, ct)));

        await shell.SubmitQueryBeginAsync(UserPrompt);
        // 迭代器是惰性的：这一信号出现才代表消费者真的进了查询流，Esc 才落在「流中打断」路径上。
        await streamEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Esc 是「输入框聚焦时」的绑定（ContextChat），必须走输入框分发链而不是 Toplevel 冒泡。
        shell.TypeKey(Key.Esc);
        await shell.AwaitQueryEndAsync();

        shell.TranscriptText.Should().Contain("已中断当前 agent", "Esc 在忙碌时是打断而不是退出");
        shell.App.DidNotReceive().RequestStop(Arg.Any<Terminal.Gui.App.IRunnable>());
        shell.Transcript.MessageView.IsStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitSecondPromptWhileFirstStreams_CancelsFirstQuery()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var shell = TuiTestShell.Create(b => b.StreamQuery((_, _, ct) =>
        {
            // 仅第一轮查询挂起；第二轮换成已结束的脚本，用于观察「新一轮取消旧一轮」。
            if (Interlocked.Increment(ref started) == 1)
                return Blocked(entered, gate.Task, ct);
            return Completed(new TuiTextDelta("第二轮回复"));
        }));

        await shell.SubmitQueryBeginAsync("第一轮");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        shell.Submit("第二轮");
        await shell.AwaitQueryEndAsync();

        shell.TranscriptText.Should().Contain("第二轮回复");
        shell.App.DidNotReceive().RequestStop(Arg.Any<Terminal.Gui.App.IRunnable>());
    }

    private static async IAsyncEnumerable<TuiEvent> Blocked(
        TaskCompletionSource streamEntered,
        Task gate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        streamEntered.TrySetResult();
        await gate.WaitAsync(ct);
        yield break;
    }

    private static async IAsyncEnumerable<TuiEvent> Completed(params TuiEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }
}
