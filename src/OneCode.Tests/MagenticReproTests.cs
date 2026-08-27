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

    /// <summary>
    /// 固化 MAF 1.19.0 ChatProtocol 语义：单成员 SequentialWorkflowBuilder 用
    /// RunStreamingAsync 直接投递消息不会触发 agent（executor 只累积对话，等 TurnToken），
    /// 表现为零 LLM 调用、turns=0、"(no output)"——这正是 TeamWorkflowRunner 曾在线上
    /// 踩到的 ParallelDag 静默失败。若 MAF 升级后本测试失败（语义改为直接执行），
    /// 可考虑把 ExecuteWorkflowAsync 简化回 RunStreamingAsync。
    /// </summary>
    [Fact]
    public async Task Sequential_DirectMessageStart_DoesNotInvokeAgent_MafChatProtocolSemantics()
    {
        var echo = new EchoChatClient();
        var agent = new ChatClientAgent(echo, name: "solo");
        var workflow = new SequentialWorkflowBuilder([agent])
            .WithName("repro")
            .Build();
        var env = InProcessExecution.Default;
        var run = await env.RunStreamingAsync(
            workflow, new ChatMessage(ChatRole.User, "do a task"), SessionId.NewId());
        var sawOutput = false;
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var evt in run.WatchStreamAsync(timer.Token))
            {
                if (evt is WorkflowOutputEvent) sawOutput = true;
            }
        }
        catch (OperationCanceledException) { }
        await run.DisposeAsync();
        Volatile.Read(ref echo.CallCount).Should().Be(0, "MAF ChatProtocol: plain ChatMessage never triggers a turn without TurnToken");
        sawOutput.Should().BeFalse("workflow completes silently without any agent output");
    }

    /// <summary>
    /// ParallelDag 修复验证：两段式启动（消息 + TurnToken）应触发 agent 执行。
    /// </summary>
    [Fact]
    public async Task Sequential_TwoPhaseStart_WithTurnToken_InvokesAgent()
    {
        var echo = new EchoChatClient();
        var agent = new ChatClientAgent(echo, name: "solo");
        var workflow = new SequentialWorkflowBuilder([agent])
            .WithName("repro")
            .Build();
        var env = InProcessExecution.Default;
        var run = await env.OpenStreamingAsync(workflow, SessionId.NewId());
        _ = await run.TrySendMessageAsync(new ChatMessage(ChatRole.User, "do a task"));
        _ = await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var sawOutput = false;
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var evt in run.WatchStreamAsync(timer.Token))
            {
                if (evt is WorkflowOutputEvent) sawOutput = true;
            }
        }
        catch (OperationCanceledException) { }
        await run.DisposeAsync();
        Volatile.Read(ref echo.CallCount).Should().BeGreaterThan(0, "TurnToken must trigger the agent turn");
        sawOutput.Should().BeTrue("workflow must produce final output");
    }

    /// <summary>
    /// 固化 MAF 1.19.0 语义：RoundRobin 群聊用 RunStreamingAsync 直接投递消息同样
    /// 不触发成员发言（与 Sequential 相同的 TurnToken 语义）——GroupChat 路径因此
    /// 也必须走两段式启动。
    /// </summary>
    [Fact]
    public async Task GroupChat_DirectMessageStart_DoesNotInvokeAgent_MafChatProtocolSemantics()
    {
        var echo = new EchoChatClient();
        var agent1 = new ChatClientAgent(echo, name: "a1");
        var agent2 = new ChatClientAgent(echo, name: "a2");
        var workflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agentList => new RoundRobinGroupChatManager(agentList, (_, _, _) => ValueTask.FromResult(true)))
            .AddParticipants([agent1, agent2])
            .WithName("repro")
            .Build();
        var env = InProcessExecution.Default;
        var run = await env.RunStreamingAsync(
            workflow, new ChatMessage(ChatRole.User, "do a task"), SessionId.NewId());
        var sawOutput = false;
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var evt in run.WatchStreamAsync(timer.Token))
            {
                if (evt is WorkflowOutputEvent) sawOutput = true;
            }
        }
        catch (OperationCanceledException) { }
        await run.DisposeAsync();
        Volatile.Read(ref echo.CallCount).Should().Be(0, "MAF ChatProtocol: group chat members never speak without TurnToken");
        sawOutput.Should().BeFalse("workflow completes silently without any agent output");
    }
}
