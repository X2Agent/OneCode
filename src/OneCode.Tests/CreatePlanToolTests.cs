using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// SubmitPlan 的 AIFunction 装配护栏：发给模型的 schema 不降级、默认编组语义不变、
/// 模型产出的非数组 steps（本次线上故障：二次编码字符串）不再在反射编组阶段抛 JsonException。
/// </summary>
public sealed class CreatePlanToolTests : IDisposable
{
    private const string PlanMarkdown =
        "# Binding Guard Plan\n\n## Context\nUpdate src/OneCode.App/Tools/CreatePlanTool.cs using the persisted workflow.\n\n## Approach\nBind steps tolerantly and persist the submitted revision.\n\n## Verification\nRun dotnet build and dotnet test.";

    private const string StepsJson =
        """
        [{"id":"binding-step","title":"Bind steps","description":"Bind the steps argument tolerantly.",
          "files":["src/OneCode.App/Tools/CreatePlanTool.cs"],"acceptanceCriteria":["Malformed shapes no longer throw."],
          "dependsOn":[],"risk":"high"}]
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodeCreatePlanToolTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateSubmitPlanFunction_KeepsTypedStepsSchema()
    {
        var (sut, _, _) = BuildSut();

        var function = CreatePlanTool.CreateSubmitPlanFunction(sut);

        function.Name.Should().Be("SubmitPlan", "权限门、能力门与 plan 协议校验都按注册名匹配工具");
        var stepsSchema = function.JsonSchema.GetProperty("properties").GetProperty("steps");
        ReadSchemaTypes(stepsSchema).Should().Contain("array",
            "steps 必须以数组类型下发给模型——改成 JsonElement 参数或 ExcludeFromSchema 都会在此变红");
        var itemsSchema = stepsSchema.GetProperty("items");
        ReadSchemaTypes(itemsSchema).Should().Contain("object");
        itemsSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                new[] { "id", "title", "description", "files", "acceptanceCriteria", "dependsOn", "risk", "assignee", "status" },
                "步骤对象对模型暴露的全部字段必须保持可见");
    }

    private static IEnumerable<string> ReadSchemaTypes(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.String
            ? [type.GetString()!]
            : type.EnumerateArray().Select(candidate => candidate.GetString()!);
    }

    [Fact]
    public async Task InvokeAsync_CamelCaseStepsArray_PersistsStepsThroughCustomBinding()
    {
        var (sut, conversation, store) = BuildSut();
        OneCodeAgentRunContext.CurrentRunId = "plan-run-binding";
        try
        {
            using var stepsDoc = JsonDocument.Parse(StepsJson);
            var function = CreatePlanTool.CreateSubmitPlanFunction(sut);

            await function.InvokeAsync(
                new AIFunctionArguments
                {
                    { "content", PlanMarkdown },
                    { "steps", stepsDoc.RootElement.Clone() },
                },
                TestContext.Current.CancellationToken);

            var steps = await LoadSubmittedStepsAsync(store, conversation);
            steps.Should().ContainSingle();
            steps[0].Id.Should().Be("binding-step");
            steps[0].Title.Should().Be("Bind steps");
            steps[0].Files.Should().Equal("src/OneCode.App/Tools/CreatePlanTool.cs");
            steps[0].AcceptanceCriteria.Should().Equal("Malformed shapes no longer throw.");
            steps[0].Risk.Should().Be(PlanStepRisk.High);
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = null;
        }
    }

    [Fact]
    public async Task InvokeAsync_DoubleEncodedStepsString_DoesNotThrowAndPersistsSubmittedPlan()
    {
        var (sut, conversation, store) = BuildSut();
        OneCodeAgentRunContext.CurrentRunId = "plan-run-encoded";
        try
        {
            // 本次故障形状：steps 是"内容为 JSON 数组的字符串"——旧编组在此抛
            // JsonException: could not be converted to PlanStepDto[]。
            // 现在由通用绑定层（ToolArgumentBindingMiddleware）先行归一化；这里按真实管线
            // 顺序先归一化，再走 SubmitPlan 的自定义参数绑定。
            var encodedSteps = JsonSerializer.Serialize(StepsJson);
            using var stepsDoc = JsonDocument.Parse(encodedSteps);
            var function = CreatePlanTool.CreateSubmitPlanFunction(sut);
            var arguments = new AIFunctionArguments
            {
                { "content", PlanMarkdown },
                { "steps", stepsDoc.RootElement.Clone() },
            };
            ToolArgumentBindingMiddleware.TryNormalizeArrayArguments(function, arguments, out _)
                .Should().BeFalse("二次编码字符串应被归一化为数组，而不是被判为不可用");
            Func<Task> act = async () => await function.InvokeAsync(
                arguments,
                TestContext.Current.CancellationToken);

            await act.Should().NotThrowAsync("二次编码的 steps 必须被容错解析，而不是中断整个 run");

            var steps = await LoadSubmittedStepsAsync(store, conversation);
            steps.Should().ContainSingle();
            steps[0].Id.Should().Be("binding-step");
            steps[0].Title.Should().Be("Bind steps");
            steps[0].Risk.Should().Be(PlanStepRisk.High);
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = null;
        }
    }

    private static async Task<IReadOnlyList<PlanStepDefinition>> LoadSubmittedStepsAsync(
        PlanAggregateStore store,
        Conversation conversation)
    {
        var aggregate = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
        aggregate.Should().NotBeNull("SubmitPlan 校验通过后必须落盘聚合");
        aggregate!.Revisions.Should().ContainSingle();
        return aggregate.Revisions[^1].Steps;
    }

    private (CreatePlanTool Sut, Conversation Conversation, PlanAggregateStore Store) BuildSut()
    {
        Directory.CreateDirectory(_root);
        var conversation = new Conversation();
        var sessions = Substitute.For<ISessionManager>();
        sessions.ForegroundConversation.Returns(conversation);
        var mode = Substitute.For<IPlanModeService>();
        mode.IsInPlanMode.Returns(true);
        var store = new PlanAggregateStore(_root);
        var workflow = new PlanWorkflowApplicationService(store);
        var sut = new CreatePlanTool(
            mode,
            new PlanCardPublisher(new OrchestrationEventBus()),
            sessions,
            workflow,
            store);
        return (sut, conversation, store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
