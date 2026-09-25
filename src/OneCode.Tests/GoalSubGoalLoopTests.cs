using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Tui;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// GOAL 子目标 <see cref="LoopAgent"/> 循环的守卫测试。
/// 覆盖：确定性硬门禁先于 AI Judge、MORE-wins 判定、fresh context 反馈回显、
/// 全局迭代上限、pending approval 早停、跨轮 token 累加与 per-run 状态隔离。
/// </summary>
public sealed class GoalSubGoalLoopTests : IDisposable
{
    private readonly string _workingDirectory;

    public GoalSubGoalLoopTests()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "onecode-goal-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task RunAsync_DeterministicGateFails_ReinvokesWithGateFeedbackAndPriorOutputNeverCallingJudge()
    {
        // 期望产物缺失 → 硬门禁每轮必败；AI Judge 根本不该被调用（省一次模型调用）。
        var judge = Substitute.For<IChatClient>();
        var inner = new ScriptedStreamingAgent("Implemented the parser and exported the symbol.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, new GoalItem
        {
            Id = 7,
            Description = "Add parser",
            SuccessCriteria = "Parser compiles",
            ExpectedFiles = ["src/Parser.cs"],
        });

        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(3, "MaxIterations 是全局硬上限，评估器无法突破");
        inner.CallCount.Should().Be(3);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // 第二轮输入必须带上轮实际产出 + 硬门禁真实失败原因，否则 fresh context 下 agent 会重复劳动。
        var secondInput = inner.MessagesPerCall[1].Select(m => m.Text);
        secondInput.Should().Contain(text => text!.Contains("Implemented the parser"));
        secondInput.Should().Contain(text => text!.Contains("## 上一轮已完成的工作"));
        secondInput.Should().Contain(text => text!.Contains("expected-artifacts"));
        secondInput.Should().Contain(text => text!.Contains("Parser.cs"));
    }

    [Fact]
    public async Task RunAsync_GatePassesAndJudgeAccepts_CompletesOnFirstIteration()
    {
        var judge = CreateJudgeClient("VERDICT: DONE");
        var inner = new ScriptedStreamingAgent("All success criteria met.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Completed);
        result.Attempts.Should().Be(1);
        result.Evaluation.Should().Be("Hard validation and semantic acceptance passed");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_JudgeReportsGap_ContinuesIterationUntilAccepted()
    {
        var judge = CreateJudgeClient(
            "VERDICT: MORE\nStill missing the null-guard path.",
            "VERDICT: DONE");
        var inner = new ScriptedStreamingAgent("Partial work.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Completed);
        result.Attempts.Should().Be(2);
        inner.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData("VERDICT: MORE\nAlmost there... VERDICT: DONE")]
    [InlineData("VERDICT: DONE then VERDICT: MORE - ambiguous reply")]
    [InlineData("the parser looks fine to me")]
    public async Task RunAsync_JudgeVerdictAmbiguous_TreatsAsIncompleteInsteadOfAccepting(string judgeReply)
    {
        // 反证点：仅当判定文本以 VERDICT: DONE 收尾才算完成；双标记/无标记时不得放行未完成子目标。
        var judge = CreateJudgeClient(judgeReply);
        var inner = new ScriptedStreamingAgent("Work in progress.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_IterationReturnsPendingToolApproval_StopsBeforeFirstEvaluation()
    {
        // LoopAgent 语义：某轮返回 pending approval 时立即停止并回传，不藏在自主迭代后面。
        var judge = Substitute.For<IChatClient>();
        var inner = ScriptedStreamingAgent.WithMessages(BuildPendingApprovalMessage());
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        inner.CallCount.Should().Be(1);
        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(1);
        result.Evaluation.Should().Be("Stopped before first evaluation (suspected pending tool approval)");
    }

    [Fact]
    public async Task RunAsync_JudgeUsageAccumulatesAcrossIterations()
    {
        var judge = CreateJudgeClient(
            usage: new UsageDetails { InputTokenCount = 11, OutputTokenCount = 4 },
            replies: ["VERDICT: MORE\nkeep going", "VERDICT: DONE"]);
        var inner = new ScriptedStreamingAgent("Work.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Completed);
        result.InputTokens.Should().Be(22);
        result.OutputTokens.Should().Be(8);
    }

    [Fact]
    public async Task RunAsync_InvokedTwice_PerRunStateDoesNotLeakBetweenRuns()
    {
        // per-run 状态必须保持在 run 作用域内；若被提升为共享/静态，第二次 run 会继承第一次的轮次与 token。
        var firstJudge = CreateJudgeClient("VERDICT: MORE", "VERDICT: MORE", "VERDICT: DONE");
        var firstInner = new ScriptedStreamingAgent("First run.");
        var first = await RunAsync(CreateLoop(firstJudge), firstInner, CreateGoal());
        first.Attempts.Should().Be(3);

        var secondJudge = CreateJudgeClient("VERDICT: DONE");
        var secondInner = new ScriptedStreamingAgent("Second run.");
        var second = await RunAsync(CreateLoop(secondJudge), secondInner, CreateGoal());

        second.Attempts.Should().Be(1);
        second.OutputTokens.Should().Be(0, "第二次 run 的 judge 无 usage，不得累计第一次的消耗");
        second.AgentOutput.Should().Be("Second run.");
    }

    [Fact]
    public async Task RunAsync_FeedbackLogAccumulatesAcrossIterations_LaterInputCarriesEveryPriorEntry()
    {
        // fresh context 下 LoopAgent 重发"原始输入 + 聚合 feedback 日志"。
        // 这里每轮都由硬门禁失败（judge 不参与），第三轮的反馈必须仍含前两轮的条目——
        // 若日志被替换成"仅最新一条"，早期失败原因会丢失，下一轮无从对照。
        var judge = Substitute.For<IChatClient>();
        var inner = new ScriptedStreamingAgent("round-1 work", "round-2 work");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, new GoalItem
        {
            Id = 9,
            Description = "Add guard",
            SuccessCriteria = "No panics",
            ExpectedFiles = ["src/Guard.cs"],
        });

        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(3);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        var feedback = inner.MessagesPerCall[2].Select(m => m.Text).Last()!;
        feedback.Split("## 仍未满足", StringSplitOptions.None).Should().HaveCount(3,
            "三轮里前两轮的失败条目都应保留在聚合日志中");
        feedback.Should().Contain("round-2 work", "第三轮要能看到上一轮刚做完的事");
    }

    private static GoalSubGoalLoop CreateLoop(IChatClient judgeClient)
        => new(
            new GoalSubGoalHardGate(),
            new GoalSubGoalJudge(judgeClient, Substitute.For<IPromptManager>()),
            NullLoggerFactory.Instance);

    private static GoalItem CreateGoal() => new()
    {
        Id = 1,
        Description = "Implement the feature",
        SuccessCriteria = "Feature works end to end",
    };

    private static ChatResponse JudgeResponse(string reply, UsageDetails? usage = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, reply);
        return usage is null
            ? new ChatResponse(message)
            : new ChatResponse(message) { Usage = usage };
    }

    private static IChatClient CreateJudgeClient(params string[] replies)
        => CreateJudgeClient(usage: null, replies: replies);

    private static IChatClient CreateJudgeClient(UsageDetails? usage, params string[] replies)
    {
        var queue = new Queue<string>(replies);
        var client = Substitute.For<IChatClient>();
        // 脚本耗尽后重复最后一条判定，保证"每轮都未完成"的用例能跑满循环上限。
        string Next() => queue.Count > 1 ? queue.Dequeue() : queue.Peek();

        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(JudgeResponse(Next(), usage)));
        return client;
    }

    private async Task<SubGoalExecution> RunAsync(
        GoalSubGoalLoop loop,
        AIAgent inner,
        GoalItem goal,
        IReadOnlyList<GoalToolExecutionEvidence>? toolExecutions = null)
    {
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
        try
        {
            return await loop.RunAsync(
                inner,
                goal,
                new GoalRunOptions
                {
                    Goal = goal.Description,
                    WorkingDirectory = _workingDirectory,
                    Tools = [],
                },
                transaction,
                channel.Writer,
                toolExecutions?.ToList() ?? [],
                "system prompt",
                $"## Sub-goal {goal.Id}: {goal.Description}",
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private static ChatMessage BuildPendingApprovalMessage()
    {
        var request = new ToolApprovalRequestContent("call-1", new FunctionCallContent("call-1", "Write", null));
        return new ChatMessage(ChatRole.Assistant, new List<AIContent> { request });
    }

    /// <summary>
    /// 按调用序号返回脚本化流式响应的最小 <see cref="AIAgent"/>。
    /// 与 <c>TestAgents</c> 同代约定：只实现抽象成员，不接任何真实模型。
    /// </summary>
    private sealed class ScriptedStreamingAgent : AIAgent
    {
        private readonly List<AgentResponseUpdate[]> _iterations;

        public ScriptedStreamingAgent(params string[] replies)
            : this(replies.Select(ToUpdates))
        {
        }

        private ScriptedStreamingAgent(IEnumerable<AgentResponseUpdate[]> iterations)
        {
            _iterations = iterations.ToList();
            MessagesPerCall = [];
        }

        /// <summary>按原始消息脚本化（用于 pending tool approval 等非文本响应）。</summary>
        public static ScriptedStreamingAgent WithMessages(params ChatMessage[] messages)
            => new(messages.Select(m => new[] { new AgentResponseUpdate(m.Role, (IList<AIContent>)m.Contents) }));

        public int CallCount => MessagesPerCall.Count;

        public List<List<ChatMessage>> MessagesPerCall { get; }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            MessagesPerCall.Add(messages.ToList());
            // 超出脚本长度时重复最后一轮响应——迭代上限由 LoopAgent 决定，不由脚本长度决定。
            var index = Math.Min(MessagesPerCall.Count - 1, _iterations.Count - 1);
            foreach (var update in _iterations[index])
                yield return update;
            await Task.Yield();
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new EmptyGoalLoopSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new EmptyGoalLoopSession());

        private static AgentResponseUpdate[] ToUpdates(string reply)
            => [new AgentResponseUpdate(ChatRole.Assistant, reply) { MessageId = Guid.NewGuid().ToString("N") }];
    }

    private sealed class EmptyGoalLoopSession : AgentSession;
}
