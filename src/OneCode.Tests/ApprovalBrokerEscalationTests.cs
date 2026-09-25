using System.Threading.Channels;
using OneCode.App.Query;
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
    /// <summary>等待下一个审批提示；超时返回 null（反证：升级后不再弹窗）。</summary>
    private static async Task<ApprovalRequestEvent?> NextPromptAsync(
        Channel<object> channel,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var surfaced = await channel.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            surfaced.Should().BeOfType<ApprovalRequestEvent>();
            return (ApprovalRequestEvent)surfaced;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task RequestAsync_UserAllowsAllForConversation_SubsequentCallsAutoApproveWithoutPrompting()
    {
        var channel = Channel.CreateUnbounded<object>();
        var broker = ApprovalBroker.ForQuery(channel.Writer);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        (await NextPromptAsync(channel, TimeSpan.FromSeconds(5)))!.ResponseSource
            .TrySetResult(ApprovalDecision.AllowAllConversation);

        var firstDecision = await firstRequest;
        firstDecision.Should().Be(ApprovalDecision.AllowAllConversation);

        var secondRequest = broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);

        // 先断言「没有新弹窗」再 await 决策：守卫回归时（第二次调用重新进入弹窗路径）
        // 用例在 2s 内失败，而不是永远挂在一个无人应答的审批上。
        (await NextPromptAsync(channel, TimeSpan.FromSeconds(2))).Should().BeNull(
            "an escalated conversation must not surface a second approval prompt in the same run");

        var secondDecision = await secondRequest;
        secondDecision.Should().Be(ApprovalDecision.AllowOnce,
            "the user already allowed every tool call for this conversation");
    }

    [Fact]
    public async Task RequestAsync_SingleApproval_DoesNotSuppressLaterPrompts()
    {
        var channel = Channel.CreateUnbounded<object>();
        var broker = ApprovalBroker.ForQuery(channel.Writer);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        (await NextPromptAsync(channel, TimeSpan.FromSeconds(5)))!.ResponseSource
            .TrySetResult(ApprovalDecision.AllowOnce);
        (await firstRequest).Should().Be(ApprovalDecision.AllowOnce);

        var secondRequest = broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);
        var secondPrompt = await NextPromptAsync(channel, TimeSpan.FromSeconds(5));

        secondPrompt.Should().NotBeNull(
            "a one-off approval must leave the next call to be reviewed again");
        secondPrompt!.ResponseSource.TrySetResult(ApprovalDecision.Deny);
        (await secondRequest).Should().Be(ApprovalDecision.Deny);
    }

    [Fact]
    public async Task RequestAsync_UserDenies_StaysInteractive()
    {
        // 反证：Deny 不是升级信号，不得触发 run 级自动放行。
        var channel = Channel.CreateUnbounded<object>();
        var broker = ApprovalBroker.ForQuery(channel.Writer);

        var firstRequest = broker.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        (await NextPromptAsync(channel, TimeSpan.FromSeconds(5)))!.ResponseSource
            .TrySetResult(ApprovalDecision.Deny);
        (await firstRequest).Should().Be(ApprovalDecision.Deny);

        var secondRequest = broker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);
        var secondPrompt = await NextPromptAsync(channel, TimeSpan.FromSeconds(5));

        secondPrompt.Should().NotBeNull(
            "a denied call must not silently escalate the conversation to allow-all");
        secondPrompt!.ResponseSource.TrySetResult(ApprovalDecision.Deny);
        (await secondRequest).Should().Be(ApprovalDecision.Deny);
    }

    [Fact]
    public async Task RequestAsync_EscalatedRun_IsIsolatedPerBrokerInstance()
    {
        // 每个 run 一个 broker：升级只影响本 run，新 run 的审批通道不得继承自动放行。
        var channel = Channel.CreateUnbounded<object>();
        var escalated = ApprovalBroker.ForQuery(channel.Writer);
        var request = escalated.RequestAsync(
            new ApprovalRequest("req-1", "Write", "{}"), TestContext.Current.CancellationToken);
        (await NextPromptAsync(channel, TimeSpan.FromSeconds(5)))!.ResponseSource
            .TrySetResult(ApprovalDecision.AllowAllConversation);
        await request;

        var freshBroker = ApprovalBroker.ForQuery(channel.Writer);
        var freshRequest = freshBroker.RequestAsync(
            new ApprovalRequest("req-2", "Bash", "{}"), TestContext.Current.CancellationToken);
        var freshPrompt = await NextPromptAsync(channel, TimeSpan.FromSeconds(5));

        freshPrompt.Should().NotBeNull(
            "session-wide escalation is owned by the permission-mode override, not by a leaked broker flag");
        freshPrompt!.ResponseSource.TrySetResult(ApprovalDecision.Deny);
        (await freshRequest).Should().Be(ApprovalDecision.Deny);
    }
}
