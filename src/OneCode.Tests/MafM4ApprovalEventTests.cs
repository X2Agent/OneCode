using OneCode.App.Query;
using OneCode.App.Tui;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// 流式审批事件化单测。
/// 验证 ApprovalRequestEvent → TuiApprovalRequest 的映射和 ResponseSource 回调桥接。
/// </summary>
public sealed class MafM4ApprovalEventTests
{
    [Fact]
    public void MapQueryEventToTuiEvent_ApprovalRequestEvent_ReturnsTuiApprovalRequest()
    {
        var evt = new ApprovalRequestEvent("req-1", "Bash", "ls -la");

        var tuiEvt = TuiEventMapper.MapQueryEventToTuiEvent(evt);

        tuiEvt.Should().BeOfType<TuiApprovalRequest>();
        var approval = (TuiApprovalRequest)tuiEvt!;
        approval.RequestId.Should().Be("req-1");
        approval.ToolName.Should().Be("Bash");
        approval.ToolInput.Should().Be("ls -la");
    }

    [Fact]
    public async Task TuiApprovalRequest_ResponseSource_BridgesBackToOriginalEvent()
    {
        var evt = new ApprovalRequestEvent("req-2", "Write", "file.txt");
        var tuiEvt = (TuiApprovalRequest)TuiEventMapper.MapQueryEventToTuiEvent(evt)!;

        // TUI 设置决策
        tuiEvt.ResponseSource.TrySetResult(ApprovalDecision.AllowAlways);

        // 原始事件应收到决策
        var decision = await evt.ResponseSource.Task.ConfigureAwait(false);
        decision.Should().Be(ApprovalDecision.AllowAlways);
    }

    [Fact]
    public async Task TuiApprovalRequest_Fault_PropagatesAsException()
    {
        var evt = new ApprovalRequestEvent("req-4", "Edit", "test.cs");
        var tuiEvt = (TuiApprovalRequest)TuiEventMapper.MapQueryEventToTuiEvent(evt)!;

        tuiEvt.ResponseSource.TrySetException(new InvalidOperationException("UI crashed"));

        var act = async () => await evt.ResponseSource.Task.ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("UI crashed");
    }
}
