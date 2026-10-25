using NSubstitute;
using OneCode.App.Query;
using OneCode.Tests.TestSupport.Tui;

namespace OneCode.Tests;

/// <summary>
/// /resume 的 TUI 派发契约：命令结果必须经 <c>CommandDispatchResult.ResumeWorkflow</c>
/// 派发到 <c>StreamResumeWorkflow</c> 恢复流。回归背景：ResumeCommand 一旦声明
/// <c>Immediate</c>，Immediate 路径的展示映射不识别 ResumeWorkflowResult（落 _ => null），
/// 恢复流会静默丢失——GOAL 唯一的 TUI 恢复入口随之失效。
/// </summary>
public sealed class OneCodeToplevelResumeTests
{
    [Fact]
    public async Task SubmitResume_InvokesResumeStream_WithSessionId()
    {
        var resumeStarted = false;
        using var shell = TuiTestShell.Create(b =>
        {
            b.Resume(new NoticeEvent("恢复中"), new DoneEvent(null, new TokenUsage(1, 1), 0, RunTerminalReason.Completed));
            b.OnResumeStarted(() => resumeStarted = true);
        });

        // /resume 不经查询流水线（无 CreateSession），不能用 SubmitQueryAsync 的会话门闩等待；
        // 直接提交后等恢复流观测点。
        shell.Submit("/resume abc123def456abcd7890abcd1234abcd");
        await shell.WaitUntilAsync(() => resumeStarted, "恢复流启动");

        resumeStarted.Should().BeTrue("/resume 必须派发到 StreamResumeWorkflow 恢复流，而不是静默丢弃");
    }

    [Fact]
    public void ResumeCommand_IsNotImmediate()
    {
        // 契约守卫：Immediate 路径不识别 ResumeWorkflowResult。此断言与派发测试互为双保险。
        new OneCode.App.Commands.ResumeCommand(Substitute.For<Core.Goals.IGoalRunStore>())
            .Immediate.Should().BeFalse();
    }
}
