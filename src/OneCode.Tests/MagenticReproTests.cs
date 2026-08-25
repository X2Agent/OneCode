using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// 最小复现：按生产代码 TeamWorkflowRunner.RunMagenticTeamAsync 的方式构建 Magentic 工作流，
/// 用假 IChatClient 验证是否能产生任何 Agent 事件（诊断 turns=0 / (no output) 问题）。
/// </summary>
public sealed class MagenticReproTests
{
    internal sealed class EchoChatClient : IChatClient
    {
        public int CallCount;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            var last = messages.LastOrDefault()?.Text ?? "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"echo: {last}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"echo: {messages.LastOrDefault()?.Text}");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// 端到端复现生产装配：用真实 TeamAgentFactory（压缩管线 + 共享上下文 + Team 权限管线）
    /// 构建成员 Agent，再跑 Magentic 工作流，验证是否产生事件。
    /// </summary>
    /// <summary>模拟生产结构：在外层 MAF Workflow 的 Executor 内部嵌套运行 Magentic 子工作流。</summary>
    internal sealed record NestedInput(string Payload);
    internal sealed record NestedOutput(int InnerEventCount, string? InnerError);

    internal sealed class NestingExecutor(string id) : Executor<NestedInput, NestedOutput>(id)
    {
        public override async ValueTask<NestedOutput> HandleAsync(
            NestedInput message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var manager = new ChatClientAgent(new EchoChatClient(), name: "orchestrator");
            var worker = new ChatClientAgent(new EchoChatClient(), name: "worker1");
            var inner = new MagenticWorkflowBuilder(manager)
                .AddParticipants([worker])
                .WithMaxRounds(4)
                .Build();

            var env = InProcessExecution.Default;
            var run = await env.RunStreamingAsync(
                inner, new ChatMessage(ChatRole.User, message.Payload), SessionId.NewId(), cancellationToken);
            var count = 0;
            string? error = null;
            try
            {
                await foreach (var evt in run.WatchStreamAsync(cancellationToken))
                    count++;
            }
            catch (Exception ex) { error = ex.GetType().Name; }
            await run.DisposeAsync();
            return new NestedOutput(count, error);
        }
    }

    [Fact]
    public async Task Magentic_TwoPhaseStart_WithAutoApprove_Completes()
    {
        var echo = new EchoChatClient();
        var manager = new ChatClientAgent(echo, name: "orchestrator");
        var worker = new ChatClientAgent(new EchoChatClient(), name: "worker1");
        var workflow = new MagenticWorkflowBuilder(manager)
            .AddParticipants([worker])
            .WithMaxRounds(4)
            .Build();
        var env = InProcessExecution.Default;
        var run = await env.OpenStreamingAsync(workflow, SessionId.NewId());
        _ = await run.TrySendMessageAsync(new ChatMessage(ChatRole.User, "do a task"));
        _ = await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var llmCalls = 0;
        var sawOutput = false;
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var evt in run.WatchStreamAsync(timer.Token))
            {
                llmCalls = Volatile.Read(ref echo.CallCount);
                if (evt is RequestInfoEvent { Request: { } pending } &&
                    pending.TryGetDataAs<MagenticPlanReviewRequest>(out var review) && review is not null)
                {
                    await run.SendResponseAsync(new ExternalResponse(
                        pending.PortInfo, pending.RequestId, new PortableValue(review.Approve())));
                }
                if (evt is WorkflowOutputEvent) sawOutput = true;
            }
        }
        catch (OperationCanceledException) { }
        await run.DisposeAsync();
        llmCalls.Should().BeGreaterThan(0);
        sawOutput.Should().BeTrue("workflow must produce final output after auto-approving the plan review");
    }
}
