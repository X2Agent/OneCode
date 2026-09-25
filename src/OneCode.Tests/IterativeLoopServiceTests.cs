using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Loop;
using OneCode.App.Tui;
using OneCode.Core.Exec;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// <c>/loop</c> 运行时循环的守卫测试：判定只认确定性证据（命令退出码 / 验证提供者），
/// 无证据时 fail-closed；失败原因必须进入进度与汇总；per-run 状态不跨 run 泄漏。
/// </summary>
public sealed class IterativeLoopServiceTests
{
    [Fact]
    public async Task RunAsync_CheckCommandFailsThenPasses_StopsOnPassingIteration()
    {
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(1, 0)),
            new LoopRunRequest("修复构建", "dotnet build", 3));

        // 第 1 轮退出码非 0 → 继续；第 2 轮退出码 0 → 停止。
        ProgressMessages(events).Should().Contain(m => m.Contains("第 1/3 轮 · 检查未通过"));
        ProgressMessages(events).Should().Contain(m => m.Contains("第 2/3 轮 · 检查通过"));
        ProgressMessages(events).Should().Contain(m => m.Contains("循环通过"));
        events.OfType<TuiError>().Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CheckCommandNeverPasses_RunsToInvocationCapAndReportsFailure()
    {
        // LoopAgent 的 MaxIterations 是"调用 inner agent 的硬上限"且先停后判，
        // 因此上限 3 = 3 次调用 + 2 次评估；最后一次调用受上限强停、不再检查。
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(1, 1, 1)),
            new LoopRunRequest("修复构建", "dotnet build", 3));

        ProgressMessages(events).Should().Contain(m => m.Contains("第 1/3 轮 · 检查未通过"));
        ProgressMessages(events).Should().Contain(m => m.Contains("第 2/3 轮 · 检查未通过"));
        ProgressMessages(events).Should().NotContain(m => m.Contains("第 3/3 轮"));
        ProgressMessages(events).Should().Contain(m => m.Contains("3 轮内未通过"));
        ProgressMessages(events).Should().NotContain(m => m.Contains("循环通过"));
    }

    [Fact]
    public async Task RunAsync_HigherCap_AllowsThirdCheckToPass()
    {
        // 上限 4 才有第 3 次检查机会——上限-1 才是可评估轮数。
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(1, 1, 0)),
            new LoopRunRequest("修复构建", "dotnet build", 4));

        ProgressMessages(events).Should().Contain(m => m.Contains("第 3/4 轮 · 检查通过"));
        ProgressMessages(events).Should().Contain(m => m.Contains("循环通过"));
    }

    [Fact]
    public async Task RunAsync_NoCheckCommandAndNoVerificationProvider_FailsClosedOnEveryIteration()
    {
        // 反证点：没有可判定证据的循环不准宣称完成——曾经只靠模型自评。
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(0), verificationProvider: null),
            new LoopRunRequest("改到对为止", null, 2));

        ProgressMessages(events).Should().Contain(m => m.Contains("循环未通过"));
        ProgressMessages(events).Should().NotContain(m => m.Contains("循环通过"));
    }

    [Fact]
    public async Task RunAsync_VerificationProviderPasses_CompletesWithoutCheckCommand()
    {
        var verification = Substitute.For<IVerificationProvider>();
        verification.VerifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationResult { Success = true, Errors = [] });

        var events = await DrainAsync(
            CreateService(verificationProvider: verification),
            new LoopRunRequest("跑通验证", null, 2));

        ProgressMessages(events).Should().Contain(m => m.Contains("循环通过"));
    }

    [Fact]
    public async Task RunAsync_VerificationProviderFails_InjectsRealErrorsIntoSummary()
    {
        var verification = Substitute.For<IVerificationProvider>();
        verification.VerifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationResult
            {
                Success = false,
                Errors = [new VerificationError("A.cs", 12, 3, "error", "CS0103 name not found")],
            });

        var events = await DrainAsync(
            CreateService(verificationProvider: verification),
            new LoopRunRequest("修好编译错误", null, 2));

        ProgressMessages(events).Should().Contain(m => m.Contains("循环未通过"));
        // 真实失败原因必须进入汇总，而不是只报"未通过"
        ProgressMessages(events).Should().Contain(m => m.Contains("CS0103"));
    }

    [Fact]
    public async Task RunAsync_CheckCommandFails_EchoesExitCodeAndOutputToSummary()
    {
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(2, 0)),
            new LoopRunRequest("修好测试", "npm test", 2));

        ProgressMessages(events).Should().Contain(m => m.Contains("退出码 2"));
        ProgressMessages(events).Should().Contain(m => m.Contains("assertion failed"));
    }

    [Fact]
    public async Task RunAsync_InnerAgentThrows_ReportsErrorInsteadOfClaimingSuccess()
    {
        var events = await DrainAsync(
            CreateService(CreateShellExecutor(0), innerAgent: new ThrowingAgent()),
            new LoopRunRequest("会崩", "exit 0", 3));

        events.OfType<TuiError>().Should().ContainSingle();
        ProgressMessages(events).Should().Contain(m => m.Contains("循环未通过"));
    }

    [Fact]
    public async Task RunAsync_RunsTwice_PerRunStateDoesNotLeakBetweenRuns()
    {
        // per-run 状态若退化为共享/静态，第二次 run 会继承第一次的轮次。
        var first = await DrainAsync(CreateService(CreateShellExecutor(1, 0)), new LoopRunRequest("第一次", "build", 3));
        ProgressMessages(first).Should().Contain(m => m.Contains("第 2/3 轮 · 检查通过"));

        var second = await DrainAsync(CreateService(CreateShellExecutor(0)), new LoopRunRequest("第二次", "build", 3));
        ProgressMessages(second).Should().Contain(m => m.Contains("第 1/3 轮 · 检查通过"));
    }

    /// <summary>
    /// 反证：循环路径一旦挂上 agent 级会话历史 provider，<c>FreshContextPerIteration</c> 就被抵消——
    /// 历史 provider 由 <c>ChatClientAgent</c> 在每次服务调用上重新查询，每轮会重新看到整份 transcript，
    /// 循环退化为「带着全部历史重试」（见 ADR 0010 禁令 7）。
    /// </summary>
    [Fact]
    public async Task RunAsync_DoesNotMountChatHistoryProvider()
    {
        var runner = Substitute.For<IMainAgentRunner>();
        runner.BuildAsAIAgentAsync(Arg.Any<MainAgentRunOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ScriptedStreamingAgent("完成"));

        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns([]);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(Path.GetTempPath());

        var service = new IterativeLoopService(runner, CreateShellExecutor(0), null, catalog, wd);
        await DrainAsync(service, new LoopRunRequest("修复构建", "exit 0", 2));

        await runner.Received(1).BuildAsAIAgentAsync(
            Arg.Is<MainAgentRunOptions>(options => options.ConversationId == null),
            Arg.Any<CancellationToken>());
    }

    private static List<string> ProgressMessages(List<TuiEvent> events)
        => events.OfType<TuiModeProgress>().Select(e => e.Message).ToList();

    private static async Task<List<TuiEvent>> DrainAsync(IIterativeLoopService service, LoopRunRequest request)
    {
        var events = new List<TuiEvent>();
        await foreach (var evt in service.RunAsync(
            "system prompt",
            request,
            "test-model",
            imagePaths: null,
            TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            events.Add(evt);
        }

        return events;
    }

    private static IIterativeLoopService CreateService(
        IShellExecutor? shellExecutor = null,
        IVerificationProvider? verificationProvider = null,
        AIAgent? innerAgent = null)
    {
        var runner = Substitute.For<IMainAgentRunner>();
        runner.BuildAsAIAgentAsync(Arg.Any<MainAgentRunOptions>(), Arg.Any<CancellationToken>())
            .Returns(innerAgent ?? new ScriptedStreamingAgent("第一轮完成了 X", "第二轮完成了 Y"));

        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns([]);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(Path.GetTempPath());

        return new IterativeLoopService(
            runner,
            shellExecutor ?? CreateShellExecutor(0),
            verificationProvider,
            catalog,
            wd);
    }

    /// <summary>按调用序号返回脚本化退出码的 shell 执行器；耗尽后固定返回 0。</summary>
    private static IShellExecutor CreateShellExecutor(params int[] exitCodes)
    {
        var queue = new Queue<int>(exitCodes);
        var shell = Substitute.For<IShellExecutor>();
        shell.ExecuteAsync(Arg.Any<ShellExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var exitCode = queue.Count > 0 ? queue.Dequeue() : 0;
                return new ShellExecutionResult(
                    exitCode == 0 ? "ok" : "assertion failed: expected 3 got 4",
                    "",
                    exitCode,
                    false);
            });
        return shell;
    }

    private sealed class ScriptedStreamingAgent : AIAgent
    {
        private readonly List<AgentResponseUpdate[]> _iterations = [];
        private int _calls;

        public ScriptedStreamingAgent(params string[] replies)
        {
            foreach (var reply in replies)
            {
                _iterations.Add(
                [
                    new AgentResponseUpdate(ChatRole.Assistant, reply) { MessageId = Guid.NewGuid().ToString("N") },
                ]);
            }
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 超出脚本长度时重复最后一轮响应——迭代上限由 LoopAgent 决定，不由脚本长度决定。
            var index = Math.Min(_calls, _iterations.Count - 1);
            _calls++;
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
            => ValueTask.FromResult<AgentSession>(new EmptyLoopSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new EmptyLoopSession());
    }

    private sealed class EmptyLoopSession : AgentSession;

    private sealed class ThrowingAgent : AIAgent
    {
        private static readonly Exception Boom = new InvalidOperationException("agent exploded");

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw Boom;

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw Boom;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw Boom;

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw Boom;

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw Boom;
    }
}
