using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// R6 行为契约：取消不是拒绝。
/// </summary>
/// <remarks>
/// 修复前 <c>RequestCoreAsync</c> 把所有 <see cref="OperationCanceledException"/> 转成
/// <see cref="ApprovalDecision.Deny"/>，于是「用户停止了运行」与「用户点了拒绝」变得不可区分：
/// 调用方看到的是一个正常的策略拒绝，run 会继续按拒绝路径收尾，而不是中止。
/// </remarks>
public sealed class ApprovalBrokerCancellationTests
{
    [Fact]
    public async Task RequestAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var broker = ApprovalBroker.ForQuery(Substitute.For<System.Threading.Channels.ChannelWriter<object>>());

        var act = () => broker.RequestAsync(new ApprovalRequest("req-1", "Write", "{}"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller asked to stop; that is not an approval decision");
    }

    /// <summary>
    /// 反证：UI 故障（非取消）必须 fail closed，不能因为「取消要传播」就一起放开。
    /// </summary>
    [Fact]
    public async Task RequestAsync_UiFailure_FailsClosed()
    {
        var writer = Substitute.For<System.Threading.Channels.ChannelWriter<object>>();
        writer.WriteAsync(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("TUI channel closed")));
        var broker = ApprovalBroker.ForQuery(writer);

        var decision = await broker.RequestAsync(new ApprovalRequest("req-2", "Write", "{}"));

        decision.Should().Be(ApprovalDecision.Deny,
            "the user was never asked, so the tool must not run");
    }
}
