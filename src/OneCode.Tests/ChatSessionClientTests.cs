using System.Runtime.CompilerServices;
using OneCode.Core.Session;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Interaction;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Commands;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.WebUI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace OneCode.Tests;

/// <summary>
/// ChatSessionClient——Web 会话客户端行为测试：
/// 斜杠命令分发（流式 prompt / 本地执行 / 未知命令建议）、普通查询路由、
/// 会话列表与切换回放、初始化时回放 BuildRun 快照。
/// </summary>
public sealed class ChatSessionClientTests
{
    private readonly ISessionManager _sessionManager = Substitute.For<ISessionManager>();
    private readonly IQueryStreamService _streams = Substitute.For<IQueryStreamService>();
    private readonly IBuildRunStore _buildRunStore = Substitute.For<IBuildRunStore>();
    private readonly IBuildRunEventStore _buildRunEventStore = Substitute.For<IBuildRunEventStore>();
    private readonly IAppStateAccessor _appStateAccessor = Substitute.For<IAppStateAccessor>();
    private readonly IModelManager _modelManager = Substitute.For<IModelManager>();
    private readonly CommandRegistry _registry = new([]);
    private readonly WebInteractionDispatcher _dispatcher = new();

    private ChatSessionClient CreateClient(Func<CancellationToken, Task<string>>? buildSystemPrompt = null)
    {
        _appStateAccessor.Current.Returns(new AppState { MainLoopModel = "model-x" });
        _modelManager.GetMainModel("model-x")
            .Returns(new ModelInfo("model-x", "anthropic", "model-x", 8192, null, 200_000));

        var deps = new WebChatDependencies(
            Runner: Substitute.For<IConversationRunner>(),
            SessionManager: _sessionManager,
            ModeController: new OneCode.App.Modes.WorkingModeController(),
            CommandRegistry: _registry,
            BuildSystemPrompt: buildSystemPrompt ?? (_ => Task.FromResult("system-prompt")),
            LoadHarness: _ => Task.FromResult("harness-prompt"),
            ModelManager: _modelManager,
            AppState: _appStateAccessor,
            CreateCommands: () => new SlashCommandPipeline(_registry),
            Streams: _streams,
            BuildRunReplay: new BuildRunReplayService(_buildRunStore, _buildRunEventStore, logger: NullLogger<BuildRunReplayService>.Instance));

        return new ChatSessionClient(
            deps,
            _dispatcher,
            new MarkdownRenderer(),
            new RunEventBridge(logger: null),
            Substitute.For<ILogger<ChatSessionClient>>());
    }

    private static IAsyncEnumerable<QueryEvent> StreamOf(params QueryEvent[] events) => ToAsyncEnumerable(events);

    private static async IAsyncEnumerable<QueryEvent> ToAsyncEnumerable(QueryEvent[] events)
    {
        foreach (var evt in events)
            yield return evt;
    }

    [Fact]
    public async Task SendAsync_PlainText_RoutesToQueryStream()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        _streams.StreamQueryAsync(Arg.Any<InteractiveSession>(), "hello", Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(StreamOf(new DoneEvent(null, null, 0, RunTerminalReason.Completed)));

        await client.SendAsync("hello");

        _streams.Received(1).StreamQueryAsync(
            Arg.Any<InteractiveSession>(), "hello", Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>());
        client.Messages.Should().Contain(m => m.Role == WebRole.User && m.Content == "hello");
        client.IsRunning.Should().BeFalse("流结束后必须退出忙碌状态");
    }

    [Fact]
    public async Task SendAsync_TextResultCommand_ExecutesLocallyWithoutStreaming()
    {
        _registry.Register(new StaticCommand("fakehelp", CommandResult.Text("fake help output")));
        var client = CreateClient();
        await client.InitializeAsync();

        await client.SendAsync("/fakehelp");

        client.Messages.Should().Contain(m => m.Role == WebRole.System && m.Content == "fake help output");
        _streams.DidNotReceive().StreamCommandPromptAsync(
            Arg.Any<InteractiveSession>(), Arg.Any<string>(), Arg.Any<string[]?>(), Arg.Any<CancellationToken>());
        client.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_PromptResultCommand_StreamsThroughCommandPromptPipeline()
    {
        _registry.Register(new StaticCommand("fakereview", CommandResult.Prompt("review this", ["tool-a"])));
        var client = CreateClient();
        await client.InitializeAsync();
        _streams.StreamCommandPromptAsync(
                Arg.Any<InteractiveSession>(), "review this", Arg.Any<string[]?>(), Arg.Any<CancellationToken>())
            .Returns(StreamOf(new TextDeltaEvent("review result"), new DoneEvent(null, null, 0, RunTerminalReason.Completed)));

        await client.SendAsync("/fakereview");

        _streams.Received(1).StreamCommandPromptAsync(
            Arg.Any<InteractiveSession>(), "review this",
            Arg.Is<string[]?>(tools => tools != null && tools.Length == 1 && tools[0] == "tool-a"),
            Arg.Any<CancellationToken>());
        client.Messages.Should().Contain(m => m.Role == WebRole.Assistant && m.Content == "review result");
    }

    [Fact]
    public async Task SendAsync_UnknownCommand_ShowsSuggestionInsteadOfQueryingLlm()
    {
        var client = CreateClient();
        await client.InitializeAsync();

        await client.SendAsync("/nosuchcommand");

        client.Messages.Should().Contain(m =>
            m.Role == WebRole.System && m.Content.Contains("Unknown command '/nosuchcommand'"));
        _streams.DidNotReceive().StreamQueryAsync(
            Arg.Any<InteractiveSession>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_ListsSessionsForSidebar()
    {
        var client = CreateClient();
        _sessionManager.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ConversationSummary(
                new SessionId("aaaaaaaa-0000-0000-0000-000000000001"), "demo", "model-x", 3,
                DateTimeOffset.Now, DateTimeOffset.Now, new TokenUsage(0, 0)) });

        await client.InitializeAsync();

        client.Sessions.Should().ContainSingle(s => s.Title == "demo" && s.MessageCount == 3);
    }

    [Fact]
    public async Task SwitchSessionAsync_RebuildsContextFromHistoryToolUses()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        var id = new SessionId("aaaaaaaa-0000-0000-0000-000000000005");
        var switched = new Conversation { Id = id, WorkingDirectory = "/tmp" };
        switched.Messages.Add(new AssistantMessage(
            "a1",
            [
                new TextBlock("读完了"),
                new ToolUseBlock("t1", "Read", """{"filePath":"src/a.cs"}"""),
                new ToolUseBlock("t2", "WebFetch", """{"url":"https://example.com/post"}"""),
            ],
            DateTimeOffset.Now));
        _sessionManager.SwitchToSessionAsync(id.ToString(), Arg.Any<CancellationToken>()).Returns(switched);
        _sessionManager.ForegroundConversation.Returns(new Conversation { Id = id });
        _sessionManager.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        await client.SwitchSessionAsync(id.ToString());

        client.ContextItems.Should().Contain(item =>
            item.Kind == WebContextKind.File && item.Target == "src/a.cs");
        client.ContextItems.Should().Contain(item =>
            item.Kind == WebContextKind.Link && item.Target == "https://example.com/post");
    }

    [Fact]
    public async Task SendAsync_WaitsForBackgroundWarmUpInsteadOfBlockingFirstScreen()
    {
        var gate = new TaskCompletionSource<string>();
        _sessionManager.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _streams.StreamQueryAsync(
                Arg.Any<InteractiveSession>(), "hello", Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(StreamOf(new DoneEvent(null, null, 0, RunTerminalReason.Completed)));
        var client = CreateClient(_ => gate.Task);

        await client.InitializeAsync();

        client.IsInitializing.Should().BeFalse("首屏初始化不得等待提示词构建");

        var send = client.SendAsync("hello");
        send.IsCompleted.Should().BeFalse("提示词未就绪时发送必须等待后台预热任务，而不是丢弃消息");

        gate.SetResult("system-prompt");
        await send;

        client.Messages.Should().Contain(m => m.Role == WebRole.User && m.Content == "hello");
    }

    [Fact]
    public async Task SwitchSessionAsync_ProjectsReturnedConversationIntoTranscript()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        var id = new SessionId("aaaaaaaa-0000-0000-0000-000000000002");
        // 切换返回的 Conversation 即投影源：客户端不得再发起第二次会话读取。
        var switched = new Conversation { Id = id, WorkingDirectory = "/tmp" };
        switched.Messages.Add(new UserMessage("u1", "上一条用户消息", DateTimeOffset.Now));
        switched.Messages.Add(new AssistantMessage("a1", [new TextBlock("上一条助手回复")], DateTimeOffset.Now));
        _sessionManager.SwitchToSessionAsync(id.ToString(), Arg.Any<CancellationToken>()).Returns(switched);
        // 切换完成后 SessionManager 的前台会话指向新会话：刷新列表时据此标记前台。
        _sessionManager.ForegroundConversation.Returns(new Conversation { Id = id });
        _sessionManager.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ConversationSummary(
                id, "switched", "model-x", 2, DateTimeOffset.Now, DateTimeOffset.Now, new TokenUsage(0, 0)) });

        var busyStates = new List<bool>();
        client.Changed += () => busyStates.Add(client.IsSwitchingSession);

        await client.SwitchSessionAsync(id.ToString());

        client.Messages.Should().Contain(m => m.Role == WebRole.User && m.Content == "上一条用户消息");
        client.Messages.Should().Contain(m => m.Role == WebRole.Assistant && m.Content == "上一条助手回复");
        client.Sessions.Should().Contain(s => s.IsForeground, "切换后侧栏必须标出新的前台会话");
        busyStates.Should().Contain(true, "切换开始必须先通知 UI 进入加载态");
        client.IsSwitchingSession.Should().BeFalse("切换结束后必须退出加载态");
    }

    [Fact]
    public async Task SwitchSessionAsync_WhileSwitching_IgnoresSecondRequest()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        var id = new SessionId("aaaaaaaa-0000-0000-0000-000000000004");
        var gate = new TaskCompletionSource<Conversation?>();
        _sessionManager.SwitchToSessionAsync(id.ToString(), Arg.Any<CancellationToken>())
            .Returns(gate.Task);
        _sessionManager.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var first = client.SwitchSessionAsync(id.ToString());
        client.IsSwitchingSession.Should().BeTrue("等待会话读取期间必须标记忙碌");

        await client.SwitchSessionAsync(id.ToString());
        gate.SetResult(new Conversation { Id = id });
        await first;

        await _sessionManager.Received(1).SwitchToSessionAsync(id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_ReplaysLatestBuildRunIntoActivities()
    {
        var conversationId = new SessionId("aaaaaaaa-0000-0000-0000-000000000003");
        var conversation = new Conversation { Id = conversationId, WorkingDirectory = "/tmp" };
        _sessionManager.EnsureActiveSessionAsync(Arg.Any<ConversationOptions>(), Arg.Any<CancellationToken>())
            .Returns(conversation);
        _sessionManager.ForegroundConversation.Returns(conversation);
        _sessionManager.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var checkpoint = MakeBuildRun(conversationId);
        _buildRunStore.LoadAsync(conversationId, Arg.Any<CancellationToken>()).Returns(checkpoint);
        _buildRunEventStore.ReplayAsync(checkpoint.Id, Arg.Any<CancellationToken>()).Returns(checkpoint);

        var client = CreateClient();
        await client.InitializeAsync();

        client.Activities.Should().Contain(a => a.Kind == "Build", "启动时必须回放最近一次 BuildRun 快照");
    }

    [Fact]
    public async Task NewSessionAsync_WhileRunning_StopsRunAndResetsTranscript()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _streams.StreamQueryAsync(
                Arg.Any<InteractiveSession>(), "hello", Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => GatedStream(gate.Task, callInfo.Arg<CancellationToken>()));

        var send = client.SendAsync("hello");
        client.IsRunning.Should().BeTrue("发送后必须进入忙碌状态等待事件流");

        await client.NewSessionAsync();

        client.IsRunning.Should().BeFalse("新建会话必须先停掉在途运行");
        send.IsCompleted.Should().BeTrue("取消必须解除事件流等待，运行任务不得悬挂");
        client.Messages.Should().ContainSingle(m =>
            m.Role == WebRole.System && m.Content.Contains("已创建新会话"),
            "旧 run 的转录必须被整体清空，只留下新会话提示");
        gate.SetResult();
        await send;
    }

    [Fact]
    public async Task CloseCurrentAsync_WhileBlockedOnApproval_ResolvesApprovalWithDenyAndStopsRun()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        _sessionManager.ForegroundConversation.Returns(
            new Conversation { Id = new SessionId("aaaaaaaa-0000-0000-0000-000000000009") });
        var approval = new ApprovalInteractionRequest("req-1", "Write");
        // 运行流卡在审批应答上且不观测取消令牌：模拟审批管线优先等待用户决策。
        // 若关闭会话不回终局决策（或只取消不回决策就等待收尾），此测试会悬挂或失败。
        _streams.StreamQueryAsync(
                Arg.Any<InteractiveSession>(), "hello", Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamAwaitingApproval(approval.ResponseSource.Task));

        var send = client.SendAsync("hello");
        _dispatcher.TrySend(approval).Should().BeTrue("活跃电路必须能收到审批请求");
        client.Pending.Should().NotBeNull();

        await client.CloseCurrentAsync();

        (await approval.ResponseSource.Task).Should().Be(ApprovalDecision.Deny,
            "关闭会话必须给挂起审批回终局决策，审批管线不得悬挂");
        client.Pending.Should().BeNull("中断卡片不得串线到关闭后的新会话");
        client.IsRunning.Should().BeFalse();
        client.Messages.Should().ContainSingle(m => m.Content.Contains("已关闭会话"));
        await send;
    }

    [Fact]
    public async Task InitializeAsync_WhenSessionSetupFails_ExitsInitializingState()
    {
        var client = CreateClient();
        _sessionManager.EnsureActiveSessionAsync(Arg.Any<ConversationOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await FluentActions.Awaiting(() => client.InitializeAsync())
            .Should().ThrowAsync<InvalidOperationException>();

        client.IsInitializing.Should().BeFalse("初始化失败也必须复位加载态，否则 UI 永久停在占位且切换被拦截");
    }

    /// <summary>被闸门挂起的事件流：模拟长查询；取消后立即以异常收尾，闸门放行后追加一条增量。</summary>
    private static async IAsyncEnumerable<QueryEvent> GatedStream(
        Task gate, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        yield return new TextDeltaEvent("late delta");
        yield return new DoneEvent(null, null, 0, RunTerminalReason.Completed);
    }

    /// <summary>只等待审批应答、不观测取消令牌的事件流：审批解除后正常收尾。</summary>
    private static async IAsyncEnumerable<QueryEvent> StreamAwaitingApproval(Task approvalResponse)
    {
        await approvalResponse;
        yield return new DoneEvent(null, null, 0, RunTerminalReason.Completed);
    }

    private static BuildRun MakeBuildRun(SessionId conversationId) => new()
    {
        Id = new BuildRunId("run-1"),
        ConversationId = conversationId,
        State = BuildRunState.Implementing,
        CreatedAt = DateTimeOffset.Now,
    };

    /// <summary>固定行为的测试命令：名称与返回结果由构造注入。</summary>
    private sealed class StaticCommand(string name, CommandResult result) : Command
    {
        public override string Name => name;
        public override string Description => "test command";
        public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default) =>
            Task.FromResult(result);
    }
}
