using OneCode.App.Services.Agent;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// 取消不是拒绝：调用方取消必须传播为 <see cref="OperationCanceledException"/>，
/// 而界面故障（非取消）必须 fail closed 成 <see cref="ApprovalDecision.Deny"/>。
/// </summary>
public sealed class ApprovalBrokerCancellationTests
{
    /// <summary>永不自行应答的端口替身：只在取消时结束。</summary>
    private sealed class HangingSink : IApprovalPromptSink
    {
        public async Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return ApprovalDecision.Deny;
        }
    }

    /// <summary>提问通道故障的端口替身。</summary>
    private sealed class ThrowingSink : IApprovalPromptSink
    {
        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("界面通道已关闭");
    }

    [Fact]
    public async Task RequestAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var broker = ApprovalBroker.ForQuery(new HangingSink());

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
        var broker = ApprovalBroker.ForQuery(new ThrowingSink());

        var decision = await broker.RequestAsync(new ApprovalRequest("req-2", "Write", "{}"));

        decision.Should().Be(ApprovalDecision.Deny,
            "the user was never asked, so the tool must not run");
    }
}
