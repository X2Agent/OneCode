using OneCode.App.Services.Agent;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// 审批通道的会话级全放行契约：用户在弹窗选择「本次对话全部允许」后，
/// 同一 run 的后续审批必须自动放行——run 的权限模式在管道装配时已固化，
/// 不在本通道内置位就会在同一个 run 内反复弹窗，违背「不再询问」的承诺。
/// 单次允许（AllowOnce）与拒绝（Deny）不得连带抑制后续弹窗。
/// </summary>
public sealed class ApprovalBrokerEscalationTests
{
    /// <summary>记录提问次数的端口替身：请求挂起，直到测试显式应答。</summary>
    private sealed class RecordingSink : IApprovalPromptSink
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource<ApprovalDecision>> _pending = [];

        public int AskCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct = default)
        {
            var response = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _pending.Add(response);
            return response.Task;
        }

        /// <summary>应答第 index 次提问；越界返回 false（反证：不应出现新的提问）。</summary>
        public bool TryAnswer(int index, ApprovalDecision decision)
        {
            TaskCompletionSource<ApprovalDecision>? pending;
            lock (_gate)
                pending = index < _pending.Count ? _pending[index] : null;

            return pending is not null && pending.TrySetResult(decision);
        }
    }

    /// <summary>等待端口收到第 count 次提问；超时即判失败。</summary>
    private static async Task WaitForAskAsync(RecordingSink sink, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sink.AskCount < count)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task RequestAsync_UserAllowsAllForConversation_SubsequentCallsAutoApproveWithoutPrompting()
    {
        var sink = new RecordingSink();
        var broker = ApprovalBroker.ForQuery(sink);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 1);

        sink.TryAnswer(0, ApprovalDecision.AllowAllConversation).Should().BeTrue();
        (await firstRequest).Should().Be(ApprovalDecision.AllowAllConversation);

        var secondDecision = await broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);

        secondDecision.Should().Be(ApprovalDecision.AllowOnce,
            "the user already allowed every tool call for this conversation");
        sink.AskCount.Should().Be(1,
            "an escalated conversation must not surface a second approval prompt in the same run");
    }

    [Fact]
    public async Task RequestAsync_SingleApproval_DoesNotSuppressLaterPrompts()
    {
        var sink = new RecordingSink();
        var broker = ApprovalBroker.ForQuery(sink);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 1);
        sink.TryAnswer(0, ApprovalDecision.AllowOnce).Should().BeTrue();
        (await firstRequest).Should().Be(ApprovalDecision.AllowOnce);

        var secondRequest = broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 2);

        sink.TryAnswer(1, ApprovalDecision.Deny).Should().BeTrue();
        (await secondRequest).Should().Be(ApprovalDecision.Deny,
            "a one-off approval must leave the next call to be reviewed again");
    }

    [Fact]
    public async Task RequestAsync_UserDenies_StaysInteractive()
    {
        // 反证：Deny 不是升级信号，不得触发 run 级自动放行。
        var sink = new RecordingSink();
        var broker = ApprovalBroker.ForQuery(sink);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 1);
        sink.TryAnswer(0, ApprovalDecision.Deny).Should().BeTrue();
        (await firstRequest).Should().Be(ApprovalDecision.Deny);

        var secondRequest = broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 2);

        sink.TryAnswer(1, ApprovalDecision.Deny).Should().BeTrue();
        (await secondRequest).Should().Be(ApprovalDecision.Deny,
            "a denied call must not silently escalate the conversation to allow-all");
    }

    [Fact]
    public async Task RequestAsync_EscalatedRun_IsIsolatedPerBrokerInstance()
    {
        // 每个 run 一个 broker：升级只影响本 run，新 run 的审批通道不得继承自动放行。
        var sink = new RecordingSink();
        var escalated = ApprovalBroker.ForQuery(sink);

        var firstRequest = escalated.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        await WaitForAskAsync(sink, 1);
        sink.TryAnswer(0, ApprovalDecision.AllowAllConversation).Should().BeTrue();
        await firstRequest;

        var freshBroker = ApprovalBroker.ForQuery(sink);
        var freshRequest = freshBroker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);

        await WaitForAskAsync(sink, 2);
        sink.TryAnswer(1, ApprovalDecision.Deny).Should().BeTrue();
        (await freshRequest).Should().Be(ApprovalDecision.Deny,
            "session-wide escalation is owned by the permission-mode override, not by a leaked broker flag");
    }
}
