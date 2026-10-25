using OneCode.Core.Session;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Commands;
using OneCode.Core.Models;
using OneCode.WebUI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    private ChatSessionClient CreateClient()
    {
        _appStateAccessor.Current.Returns(new AppState { MainLoopModel = "model-x" });
        _modelManager.GetMainModel("model-x")
            .Returns(new ModelInfo("model-x", "anthropic", "model-x", 8192, null, 200_000));

        var deps = new WebChatDependencies(
            Runner: Substitute.For<IConversationRunner>(),
            SessionManager: _sessionManager,
            ModeController: new OneCode.App.Modes.WorkingModeController(),
            CommandRegistry: _registry,
            BuildSystemPrompt: _ => Task.FromResult("system-prompt"),
            LoadHarness: _ => Task.FromResult("harness-prompt"),
            ModelManager: _modelManager,
            AppState: _appStateAccessor,
            CreateCommands: () => new SlashCommandPipeline(_registry),
            Streams: _streams,
            BuildRunReplay: new BuildRunReplayService(_buildRunStore, _buildRunEventStore, logger: NullLogger<BuildRunReplayService>.Instance));

        return new ChatSessionClient(
            deps,
            new WebInteractionDispatcher(),
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
    public async Task SwitchSessionAsync_ReplaysForegroundHistoryIntoTranscript()
    {
        var client = CreateClient();
        await client.InitializeAsync();
        var id = new SessionId("aaaaaaaa-0000-0000-0000-000000000002");
        _sessionManager.SwitchToSessionAsync(id.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Conversation { Id = id, WorkingDirectory = "/tmp" });
        // 切换完成后 SessionManager 的前台会话指向新会话：刷新列表时据此标记前台。
        _sessionManager.ForegroundConversation.Returns(new Conversation { Id = id });
        _sessionManager.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ConversationSummary(
                id, "switched", "model-x", 2, DateTimeOffset.Now, DateTimeOffset.Now, new TokenUsage(0, 0)) });
        _sessionManager.ReplayAsync(id.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Message[]
            {
                new UserMessage("u1", "上一条用户消息", DateTimeOffset.Now),
                new AssistantMessage("a1", [new TextBlock("上一条助手回复")], DateTimeOffset.Now),
            });

        await client.SwitchSessionAsync(id.ToString());

        client.Messages.Should().Contain(m => m.Role == WebRole.User && m.Content == "上一条用户消息");
        client.Messages.Should().Contain(m => m.Role == WebRole.Assistant && m.Content == "上一条助手回复");
        client.Sessions.Should().Contain(s => s.IsForeground, "切换后侧栏必须标出新的前台会话");
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
