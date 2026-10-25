using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Query;
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
        // 修改越界 → change-scope 硬门禁每轮必败；AI Judge 根本不该被调用（省一次模型调用）。
        var judge = Substitute.For<IChatClient>();
        var inner = new ScriptedStreamingAgent("Implemented the parser and exported the symbol.");
        var loop = CreateLoop(judge);
        var outOfScopeFile = Path.Combine(_workingDirectory, "forbidden", "Parser.cs");

        var result = await RunAsync(loop, inner, new GoalItem
        {
            Id = 7,
            Description = "Add parser",
            SuccessCriteria = "Parser compiles",
            AllowedPaths = ["src"],
        }, modifiedFiles: [outOfScopeFile]);

        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(3, "MaxIterations 是全局硬上限，评估器无法突破");
        inner.CallCount.Should().Be(3);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // 第二轮输入必须带上轮实际产出 + 硬门禁真实失败原因。
        var secondInput = inner.MessagesPerCall[1].Select(m => m.Text);
        secondInput.Should().Contain(text => text!.Contains("Implemented the parser"));
        secondInput.Should().Contain(text => text!.Contains("## 上一轮已完成的工作"));
        secondInput.Should().Contain(text => text!.Contains("change-scope"));
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
    public async Task RunAsync_InnerAgentUsage_AccumulatesIntoExecutionTokens()
    {
        // 回归锚定：M.E.AI 契约是 usage 聚合在 response.Usage 上、不留在 Messages 里
        // （MAF LoopAgentTests: AggregatedTranscript_DoesNotDuplicateUsageIntoMessages）。
        // 旧实现从 Messages 扫 UsageContent 是死代码——inner 迭代的 token 全部漏计，
        // Goal 预算的 token 轴只剩 judge 调用。修复后必须从 LastResponse.Usage 读取。
        var inner = ScriptedStreamingAgent.WithRawUpdates(
            [[
                new AgentResponseUpdate(ChatRole.Assistant, "iteration-1 work")
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                },
                new AgentResponseUpdate(ChatRole.Assistant, [new UsageContent(
                    new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12 })])
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                },
            ]]);
        var judge = CreateJudgeClient("VERDICT: DONE");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Completed);
        result.InputTokens.Should().Be(30, "inner 迭代的 usage 必须从 response.Usage 读取并计入");
        result.OutputTokens.Should().Be(12);
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
    public async Task RunAsync_RetriesAcrossIterations_PreservesSessionContextAndInjectsDeltaFeedback()
    {
        // FreshContextPerIteration = false：重试时必须复用同一个 AgentSession 以保留探索上下文与工具输出，
        // 并按轮注入增量反馈，避免每轮清空会话导致失忆。
        var judge = Substitute.For<IChatClient>();
        var inner = new ScriptedStreamingAgent("round-1 work", "round-2 work");
        var loop = CreateLoop(judge);
        var outOfScopeFile = Path.Combine(_workingDirectory, "forbidden", "Guard.cs");

        var result = await RunAsync(loop, inner, new GoalItem
        {
            Id = 9,
            Description = "Add guard",
            SuccessCriteria = "No panics",
            AllowedPaths = ["src"],
        }, modifiedFiles: [outOfScopeFile]);

        result.Status.Should().Be(GoalStatus.Failed);
        result.Attempts.Should().Be(3);
        await judge.DidNotReceiveWithAnyArgs().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        inner.SessionsPerCall.Should().HaveCount(3);
        inner.SessionsPerCall.Distinct().Should().ContainSingle(
            "FreshContextPerIteration = false 必须在同一子目标的重试间复用同一个 AgentSession");

        var round2Feedback = inner.MessagesPerCall[1].Select(m => m.Text).Last()!;
        round2Feedback.Should().Contain("round-1 work");
        round2Feedback.Should().Contain("change-scope");

        var round3Feedback = inner.MessagesPerCall[2].Select(m => m.Text).Last()!;
        round3Feedback.Should().Contain("round-2 work", "第三轮要能看到上一轮刚做完的事");
        round3Feedback.Should().Contain("change-scope");
    }

    [Fact]
    public async Task RunAsync_HistoricalToolErrorWithValidDeliverable_PassesHardGateAndCompletes()
    {
        // 回归锚定：Coding 场景中探索性搜索失败或首次编译报错后修复，在历史工具记录中会留下 IsError=true；
        // 只要 Judge 判定通过，不得因历史工具错误而拒绝子目标。
        var targetFile = Path.Combine(_workingDirectory, "report.html");
        File.WriteAllText(targetFile, "<!doctype html><html><body>OK</body></html>");

        var judge = CreateJudgeClient("VERDICT: DONE");
        var inner = new ScriptedStreamingAgent("Fixed the issue and generated report.html.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(
            loop,
            inner,
            new GoalItem
            {
                Id = 10,
                Description = "Generate HTML report",
                SuccessCriteria = "report.html exists and is non-empty",
            },
            toolExecutions:
            [
                new GoalToolExecutionEvidence("WebFetch", IsError: true, "HTTP 403 Forbidden"),
                new GoalToolExecutionEvidence("Write", IsError: false, "Wrote report.html"),
            ],
            modifiedFiles: [targetFile]);

        result.Status.Should().Be(GoalStatus.Completed);
        result.Attempts.Should().Be(1);
        result.Evidence!.ChangedFiles.Should().ContainSingle()
            .Which.Should().Be(Path.GetFullPath(targetFile),
                "SubGoalEvidence.ChangedFiles 必须保留规范化绝对路径，防止子目录偏移在回执阶段丢失");
    }

    [Fact]
    public async Task RunAsync_RepeatedStreamingUsageChunksInSameTurn_DeduplicatesInsteadOfMultiplying()
    {
        // 回归锚定：StepFun 等 OpenAI 兼容流式接口在每个 SSE chunk 上附带当轮累计 UsageContent。
        // MAF ToAgentResponse() 会把每个 chunk 的 UsageContent 累加，导致单轮 Token 虚增千倍触发假预算超限。
        // GoalSubGoalLoop 必须按轮次边界去重，同轮取快照最大值，跨工具轮次再累加。
        var responseId = "resp-turn-1";
        var inner = ScriptedStreamingAgent.WithRawUpdates(
            [[
                new AgentResponseUpdate(ChatRole.Assistant, "chunk-1 ")
                {
                    ResponseId = responseId,
                    Contents = [new TextContent("chunk-1 "), new UsageContent(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 10 })],
                },
                new AgentResponseUpdate(ChatRole.Assistant, "chunk-2 ")
                {
                    ResponseId = responseId,
                    Contents = [new TextContent("chunk-2 "), new UsageContent(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 25 })],
                },
                new AgentResponseUpdate(ChatRole.Assistant, "chunk-3")
                {
                    ResponseId = responseId,
                    Contents = [new TextContent("chunk-3"), new UsageContent(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 40 })],
                },
            ]]);
        var judge = CreateJudgeClient("VERDICT: DONE");
        var loop = CreateLoop(judge);

        var result = await RunAsync(loop, inner, CreateGoal());

        result.Status.Should().Be(GoalStatus.Completed);
        result.InputTokens.Should().Be(1000, "同一轮次 3 个 SSE chunk 重复上报的 1000 InputTokens 不得被累加为 3000");
        result.OutputTokens.Should().Be(40, "同一轮次的累计 OutputTokens 应取该轮最终快照 40 而非 10+25+40=75");
    }

    [Fact]
    public async Task RunAsync_SingleFileAllowedPath_AllowsSiblingHelperFileInSameDirectory()
    {
        // 回归锚定：当分解器误将单个输出文件名（如 "news-data.json"）填入 AllowedPaths 时，
        // Agent 在同一工作目录下创建的辅助脚本（如 "extract_article.py"）不得被 change-scope 误判越界。
        var helperScript = Path.Combine(_workingDirectory, "extract_article.py");
        File.WriteAllText(helperScript, "print('ok')");

        var judge = CreateJudgeClient("VERDICT: DONE");
        var inner = new ScriptedStreamingAgent("Extracted data using helper script.");
        var loop = CreateLoop(judge);

        var result = await RunAsync(
            loop,
            inner,
            new GoalItem
            {
                Id = 11,
                Description = "Collect news",
                SuccessCriteria = "News collected",
                AllowedPaths = ["news-data.json"],
            },
            modifiedFiles: [helperScript]);

        result.Status.Should().Be(GoalStatus.Completed);
        result.Evidence!.Validations.Should().Contain(v => v.Gate == "change-scope" && v.Passed);
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
        IReadOnlyList<GoalToolExecutionEvidence>? toolExecutions = null,
        IReadOnlyList<string>? modifiedFiles = null)
    {
        var channel = Channel.CreateUnbounded<QueryEvent>();
        var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
        if (modifiedFiles is not null && inner is ScriptedStreamingAgent scripted)
        {
            scripted.OnInvoke = () =>
            {
                foreach (var file in modifiedFiles)
                    transaction.Snapshot(file);
            };
        }
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
            SessionsPerCall = [];
        }

        /// <summary>按原始消息脚本化（用于 pending tool approval 等非文本响应）。</summary>
        public static ScriptedStreamingAgent WithMessages(params ChatMessage[] messages)
            => new(messages.Select(m => new[] { new AgentResponseUpdate(m.Role, (IList<AIContent>)m.Contents) }));

        /// <summary>按原始 update 脚本化（用于注入 <see cref="UsageContent"/> 等聚合期内容）。</summary>
        public static ScriptedStreamingAgent WithRawUpdates(params AgentResponseUpdate[][] iterations)
            => new(iterations);

        public int CallCount => MessagesPerCall.Count;

        public List<List<ChatMessage>> MessagesPerCall { get; }

        public List<AgentSession?> SessionsPerCall { get; }

        public Action? OnInvoke { get; set; }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            OnInvoke?.Invoke();
            MessagesPerCall.Add(messages.ToList());
            SessionsPerCall.Add(session);
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
