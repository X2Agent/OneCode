using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Streaming;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

/// <summary>
/// Guards the todo-strip projection path: agent chain → <c>GetService&lt;TodoProvider&gt;</c>
/// → authoritative snapshot → unified event bus.
/// </summary>
/// <remarks>
/// The <c>GetService</c> hop is the fragile link: it depends on the MAF contract that every
/// <c>DelegatingAIAgent</c> decorator forwards service requests to the agent it wraps (the same
/// contract <c>TodoCompletionLoopEvaluator</c> relies on). If it breaks, the TUI strip silently
/// stops updating while the agent run itself stays green — only this test would notice.
/// </remarks>
public sealed class TodoProjectionServiceTests
{
    [Fact]
    public async Task PublishAsync_ResolvesProviderThroughMiddlewareChain_AndPublishesSnapshot()
    {
        var events = new List<OrchestrationEvent>();
        var bus = new OrchestrationEventBus();
        using var subscription = bus.Subscribe(evt => events.Add(evt));

        var harness = CreateHarnessAgent(enableTodo: true);

        // 复刻产品管线形态：middleware 装饰链包在 HarnessAgent 之外。
        var pipelined = harness.AsBuilder()
            .Use(
                runFunc: null,
                runStreamingFunc: (messages, agentSession, runOptions, agent, ct)
                    => agent.RunStreamingAsync(messages, agentSession, runOptions, ct))
            .Build();

        // ChatClientAgentSession 的构造函数是 internal——会话只能经 agent 创建，
        // 这与产品路径（AgentSessionPersistence.CreateOrRestoreSessionAsync）一致。
        var session = await pipelined.CreateSessionAsync();

        var first = await AddTodoAsync(pipelined, session, "Write the migration", "three files");
        var second = await AddTodoAsync(pipelined, session, "Run the test suite", description: null);
        await CompleteTodoAsync(pipelined, session, first);

        var sut = new TodoProjectionService(bus, NullLogger<TodoProjectionService>.Instance);
        await sut.PublishAsync(pipelined, session);

        var projection = events.Should().ContainSingle().Which
            .Should().BeOfType<OrchestrationEvent.TodoProjectionChanged>().Subject;
        projection.Items.Should().HaveCount(2);

        projection.Items[0].Id.Should().Be(first);
        projection.Items[0].Title.Should().Be("Write the migration");
        projection.Items[0].Description.Should().Be("three files");
        projection.Items[0].IsComplete.Should().BeTrue();

        projection.Items[1].Id.Should().Be(second);
        projection.Items[1].Title.Should().Be("Run the test suite");
        projection.Items[1].Description.Should().BeNull();
        projection.Items[1].IsComplete.Should().BeFalse();
    }

    /// <summary>
    /// Profile 未启用 Todo（Explore/Plan 只读 sub-agent）时 GetService 解析不到 provider：
    /// 必须发布空清单让 TUI 隐藏横条，而不是保留上一个 agent 的陈旧快照。
    /// </summary>
    [Fact]
    public async Task PublishAsync_ProfileWithoutTodo_PublishesEmptyList()
    {
        var events = new List<OrchestrationEvent>();
        var bus = new OrchestrationEventBus();
        using var subscription = bus.Subscribe(evt => events.Add(evt));

        var agent = CreateHarnessAgent(enableTodo: false);
        var session = await agent.CreateSessionAsync();

        var sut = new TodoProjectionService(bus, NullLogger<TodoProjectionService>.Instance);
        await sut.PublishAsync(agent, session);

        var projection = events.Should().ContainSingle().Which
            .Should().BeOfType<OrchestrationEvent.TodoProjectionChanged>().Subject;
        projection.Items.Should().BeEmpty();
    }

    /// <summary>
    /// 取消后不得发布快照：一轮被用户中断时清单处于中间态，
    /// 发布它会让横条显示与持久化状态不一致的瞬时数据。
    /// </summary>
    [Fact]
    public async Task PublishAsync_CancelledToken_SkipsPublish()
    {
        var events = new List<OrchestrationEvent>();
        var bus = new OrchestrationEventBus();
        using var subscription = bus.Subscribe(evt => events.Add(evt));

        var agent = CreateHarnessAgent(enableTodo: true);
        var session = await agent.CreateSessionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = new TodoProjectionService(bus, NullLogger<TodoProjectionService>.Instance);
        await sut.PublishAsync(agent, session, cts.Token);

        events.Should().BeEmpty();
    }

    private static HarnessAgent CreateHarnessAgent(bool enableTodo)
        => new(Substitute.For<IChatClient>(), new HarnessAgentOptions
        {
            Name = "todo-projection-test",
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            DisableTodoProvider = !enableTodo,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableWebSearch = true,
        });

    private static async Task<int> AddTodoAsync(
        AIAgent agent, AgentSession session, string title, string? description)
    {
        var add = await GetToolAsync(agent, session, "todos_add");
        var result = await add.InvokeAsync(BuildArguments(
            new { todos = new[] { new { title, description } } }));

        var json = JsonSerializer.Serialize(result);
        return JsonDocument.Parse(json).RootElement[0].GetProperty("id").GetInt32();
    }

    private static async Task CompleteTodoAsync(AIAgent agent, AgentSession session, int id)
    {
        var complete = await GetToolAsync(agent, session, "todos_complete");
        _ = await complete.InvokeAsync(BuildArguments(
            new { items = new[] { new { id, reason = "done in test" } } }));
    }

    private static async Task<AIFunction> GetToolAsync(AIAgent agent, AgentSession session, string toolName)
    {
        var provider = agent.GetService<TodoProvider>()
            ?? throw new InvalidOperationException(
                "TodoProvider must resolve through the agent chain (MAF GetService forwarding contract).");
        var context = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        var aiContext = await provider.InvokingAsync(context);
        return aiContext.Tools!.OfType<AIFunction>().First(tool => tool.Name == toolName);
    }

    /// <summary>按 wire 形态构造参数（JsonElement），与真实模型调用路径一致。</summary>
    private static AIFunctionArguments BuildArguments(object payload)
    {
        var arguments = new AIFunctionArguments();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }
}
