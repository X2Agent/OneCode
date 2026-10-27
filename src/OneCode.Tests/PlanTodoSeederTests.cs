#pragma warning disable MAAI001 // MAF 实验性 API（AIContextProvider.InvokingContext 构造）：升级 MAF 时按升级复查清单回归
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.Core.Build;

namespace OneCode.Tests;

/// <summary>
/// 计划步骤预置进 Harness 待办的契约：预置状态必须是 provider 真实可读写的状态
/// （勾选、剩余查询、会话序列化往返、历史失效后仍保留），否则「待办是步骤进度唯一事实源」不成立。
/// </summary>
public sealed class PlanTodoSeederTests
{
    private static readonly BuildPlanTask[] Steps =
    [
        Step("s1", "Collect sources", "Search the web for sources"),
        Step("s2", "Write report", "Produce report.html"),
    ];

    [Fact]
    public async Task SeedAsync_PopulatesProviderState_ThatTodosCompleteCanTick()
    {
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var seeded = await PlanTodoSeeder.SeedAsync(agent, session, Steps, TestContext.Current.CancellationToken);

        seeded.Should().BeTrue();
        var provider = agent.GetService<TodoProvider>()!;
        var all = await provider.GetAllTodosAsync(session, TestContext.Current.CancellationToken);
        all.Select(item => item.Title).Should().Equal("s1: Collect sources", "s2: Write report");
        all[1].Description.Should().Contain("Produce report.html").And.Contain("Acceptance: done");

        await CompleteTodoAsync(agent, session, all[0].Id);

        var remaining = await provider.GetRemainingTodosAsync(session, TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle().Which.Title.Should().Be("s2: Write report",
            "the model's own todos_complete call must tick the seeded step");
    }

    [Fact]
    public async Task SeedAsync_NonEmptyList_KeepsProgressInsteadOfResetting()
    {
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await PlanTodoSeeder.SeedAsync(agent, session, Steps, TestContext.Current.CancellationToken);
        var provider = agent.GetService<TodoProvider>()!;
        var first = (await provider.GetAllTodosAsync(session, TestContext.Current.CancellationToken))[0];
        await CompleteTodoAsync(agent, session, first.Id);

        var reseeded = await PlanTodoSeeder.SeedAsync(agent, session, Steps, TestContext.Current.CancellationToken);

        reseeded.Should().BeFalse("recovery re-entry must not wipe ticked steps");
        (await provider.GetRemainingTodosAsync(session, TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which.Title.Should().Be("s2: Write report");
    }

    [Fact]
    public async Task SeededState_SurvivesSessionSerializationAndTargetedInvalidation()
    {
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await PlanTodoSeeder.SeedAsync(agent, session, Steps, TestContext.Current.CancellationToken);
        var provider = agent.GetService<TodoProvider>()!;
        var first = (await provider.GetAllTodosAsync(session, TestContext.Current.CancellationToken))[0];
        await CompleteTodoAsync(agent, session, first.Id);

        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        var conversation = new Conversation { Id = SessionId.NewId() };
        conversation.Metadata["mafSession"] = serialized;
        conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey] = 0;

        // 恢复路径用定向失效：只丢历史绑定键，待办进度必须保留。
        MafSessionInvalidator.Invalidate(conversation, "recovery-test");
        var restored = await agent.DeserializeSessionAsync(
            (JsonElement)conversation.Metadata["mafSession"]!,
            cancellationToken: TestContext.Current.CancellationToken);

        (await provider.GetRemainingTodosAsync(restored, TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which.Title.Should().Be("s2: Write report");
    }

    [Theory]
    [InlineData("s1: Collect sources", "s1")]
    [InlineData("step-2: Write: the report", "step-2")]
    [InlineData("Buy milk", null)]
    [InlineData(": leading separator", null)]
    public void TryParseStepId_RecoversPlanStepIdOnly(string title, string? expected)
        => PlanTodoSeeder.TryParseStepId(title).Should().Be(expected);

    /// <summary>底栏去重只过滤真正由计划预置的待办：前缀与标题都要对上，模型自加的「Note: …」不能被误吞。</summary>
    [Theory]
    [InlineData("s1: Collect sources", true)]
    [InlineData("s2: Write report", true)]
    [InlineData("Note: remember the style guide", false)]
    [InlineData("s1: Collect other sources", false)]
    [InlineData("Collect sources", false)]
    public void IsSeededFor_MatchesSeededTitlesOnly(string todoTitle, bool expected)
        => PlanTodoSeeder.IsSeededFor(todoTitle, ["Collect sources", "Write report"]).Should().Be(expected);

    [Fact]
    public void IsSeededFor_RecognizesTitlesProducedByFormatTitle()
        => PlanTodoSeeder.IsSeededFor(PlanTodoSeeder.FormatTitle(Steps[1]), ["Collect sources", "Write report"])
            .Should().BeTrue("the filter must accept exactly what the seeder writes");

    [Fact]
    public async Task CompleteRemainingAsync_CompletesUntickedItemsAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync(ct);
        await PlanTodoSeeder.SeedAsync(agent, session, Steps, ct);
        var provider = agent.GetService<TodoProvider>()!;
        var allBefore = await provider.GetAllTodosAsync(session, ct);
        await CompleteTodoAsync(agent, session, allBefore[0].Id);

        var completedCount = await PlanTodoSeeder.CompleteRemainingAsync(agent, session, ct);

        completedCount.Should().Be(1, "only the remaining unticked step should be completed on closeout");
        (await provider.GetRemainingTodosAsync(session, ct)).Should().BeEmpty();
        (await provider.GetAllTodosAsync(session, ct)).Should().OnlyContain(item => item.IsComplete);

        var secondCallCount = await PlanTodoSeeder.CompleteRemainingAsync(agent, session, ct);
        secondCallCount.Should().Be(0);
    }

    private static BuildPlanTask Step(string id, string title, string description)
        => new(id, title, description, [], [], ["done"]);

    private static HarnessAgent CreateAgent() => new(Substitute.For<IChatClient>(), new HarnessAgentOptions
    {
        Name = "plan-todo-seeder-test",
        ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        DisableToolAutoApproval = true,
        DisableOpenTelemetry = true,
        DisableAgentModeProvider = true,
        DisableAgentSkillsProvider = true,
        DisableFileMemory = true,
        DisableWebSearch = true,
    });

    private static async Task CompleteTodoAsync(AIAgent agent, AgentSession session, int id)
    {
        var provider = agent.GetService<TodoProvider>()!;
        var context = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        var aiContext = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
        var complete = aiContext.Tools!.OfType<AIFunction>().First(tool => tool.Name == "todos_complete");

        var arguments = new AIFunctionArguments();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new { items = new[] { new { id, reason = "done in test" } } }));
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();
        _ = await complete.InvokeAsync(arguments, TestContext.Current.CancellationToken);
    }
}
