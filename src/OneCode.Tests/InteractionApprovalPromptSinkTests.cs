using NSubstitute;
using OneCode.App.Services.Interaction;
using OneCode.Infrastructure;
using OneCode.Core.Config;
using OneCode.Core.Permissions;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// InteractionApprovalPromptSink——端口级契约测试（走真实 TuiInteractionDispatcher 通道）：
/// 无接收者 fail-closed Deny、决策回传与重复应答 no-op、Core 请求字段投影、取消传播、
/// 挂起超宽限发桌面通知（响应/取消则撤销）。
/// </summary>
public sealed class InteractionApprovalPromptSinkTests
{
    private readonly INotifierService _notifier = Substitute.For<INotifierService>();

    [Fact]
    public async Task AskAsync_NoReceiver_ReturnsDeny()
    {
        var sink = CreateSink(new HeadlessInteractionDispatcher());

        var decision = await sink.AskAsync(CreateRequest());

        decision.Should().Be(ApprovalDecision.Deny,
            "审批是危险操作的唯一闸门，无人可问时必须 fail-closed");
    }

    [Fact]
    public async Task AskAsync_ReceiverApproves_ReturnsDecision()
    {
        ApprovalInteractionRequest? delivered = null;
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(request =>
        {
            delivered = (ApprovalInteractionRequest)request;
            delivered.ResponseSource.TrySetResult(ApprovalDecision.AllowOnce);
            // 重复应答必须被忽略：TaskCompletionSource 只接受一次结果（ADR 审批约束），
            // 第二次 TrySetResult 返回 false 且不影响已定结果。
            delivered.ResponseSource.TrySetResult(ApprovalDecision.Deny).Should().BeFalse();
        });
        var sink = CreateSink(dispatcher);

        var decision = await sink.AskAsync(CreateRequest());

        decision.Should().Be(ApprovalDecision.AllowOnce, "重复应答不得覆盖首次决策");
        delivered.Should().NotBeNull();
        delivered!.RequestId.Should().Be("req-1");
        delivered.ToolName.Should().Be("Bash");
        delivered.ToolInput.Should().Be(ShellInput);
        delivered.AgentName.Should().Be("main", "Core 请求的代理名必须投影到交互请求");
        delivered.Reason.Should().Be("危险命令", "Core 请求的原因必须投影到交互请求");
    }

    [Fact]
    public async Task AskAsync_CancelledWhileWaiting_PropagatesCancellation()
    {
        // 取消传播是审批契约的一部分：Broker 收到 OCE 后 fail-closed 映射为 Deny。
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(_ =>
        {
            // 不回传决策，等待取消
        });
        var sink = CreateSink(dispatcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => sink.AskAsync(CreateRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AskAsync_PendingBeyondGrace_SendsDesktopNotification()
    {
        var notifySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _notifier.SendNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                notifySent.TrySetResult();
                return Task.CompletedTask;
            });
        ApprovalInteractionRequest? delivered = null;
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(request => delivered = (ApprovalInteractionRequest)request);
        var sink = CreateSink(dispatcher, pendingNotifyGrace: TimeSpan.FromMilliseconds(50));

        var askTask = sink.AskAsync(CreateRequest());
        await notifySent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        delivered!.ResponseSource.TrySetResult(ApprovalDecision.AllowOnce);

        (await askTask).Should().Be(ApprovalDecision.AllowOnce);
        await _notifier.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Is<string>(m => m.Contains("Bash")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RespondsWithinGrace_DoesNotNotify()
    {
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(request =>
            ((ApprovalInteractionRequest)request).ResponseSource.TrySetResult(ApprovalDecision.AllowOnce));
        var sink = CreateSink(dispatcher, pendingNotifyGrace: TimeSpan.FromMilliseconds(200));

        var decision = await sink.AskAsync(CreateRequest());
        decision.Should().Be(ApprovalDecision.AllowOnce);
        // 宽限之外仍无通知——决策已回传，挂起通知被撤销。
        await Task.Delay(300);
        await _notifier.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private InteractionApprovalPromptSink CreateSink(
        IInteractionDispatcher dispatcher, TimeSpan? pendingNotifyGrace = null)
    {
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings { NotificationsEnabled = true }));
        return new InteractionApprovalPromptSink(dispatcher, _notifier, config, pendingNotifyGrace);
    }

    private static ApprovalRequest CreateRequest() =>
        new("req-1", "Bash", ShellInput, AgentName: "main", Reason: "危险命令");

    private const string ShellInput = """{"command":"dotnet build"}""";
}