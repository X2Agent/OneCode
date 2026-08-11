using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.App.Tui;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>Regression coverage for versioned plan persistence through CreatePlanTool.</summary>
public sealed class PlanCardStatePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodePlanToolTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavePlanAsync_PersistsVersionedDraftWithoutLegacyMetadata()
    {
        var (sut, conversation, workflow) = BuildSut();

        var result = await sut.SavePlanAsync(CreatePlanMarkdown(), [CreateStep()],
            TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        var restored = await workflow.GetAsync(conversation.Id, TestContext.Current.CancellationToken);
        restored!.State.Should().Be(PlanWorkflowState.Planning);
        restored.LatestRevision.Should().Be(1);
        conversation.Metadata.Should().NotContainKey(PlanCardStateKeys.PlanCard);
    }

    [Fact]
    public async Task SubmitPlanAsync_ReturnsWithoutWaitingAndPersistsFinalizingRun()
    {
        var (sut, conversation, workflow) = BuildSut();
        OneCodeAgentRunContext.CurrentRunId = "plan-run-1";
        try
        {
            var result = await sut.SubmitPlanAsync(CreatePlanMarkdown(), [CreateStep()],
                TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            var restored = await workflow.GetAsync(conversation.Id, TestContext.Current.CancellationToken);
            restored!.State.Should().Be(PlanWorkflowState.FinalizingPlanRun);
            restored.ActiveRunId.Should().Be("plan-run-1");
            restored.SubmittedRevision.Should().Be(1);
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = null;
        }
    }

    private (CreatePlanTool Sut, Conversation Conversation, IPlanWorkflowApplicationService Workflow) BuildSut()
    {
        var conversation = new Conversation();
        var sessions = Substitute.For<ISessionManager>();
        sessions.ForegroundConversation.Returns(conversation);
        var mode = Substitute.For<IPlanModeService>();
        mode.IsInPlanMode.Returns(true);
        var workflow = new PlanWorkflowApplicationService(new PlanAggregateStore(_root));
        return (new CreatePlanTool(mode, new PlanCardPublisher(), sessions, workflow), conversation, workflow);
    }

    private static string CreatePlanMarkdown() =>
        "# Persisted Plan Workflow\n\n## Context\nUpdate src/OneCode.App/Tools/CreatePlanTool.cs using the persisted workflow.\n\n## Approach\nStore immutable revisions and execute approved structured steps.\n\n## Verification\nRun dotnet build and dotnet test.";

    private static PlanStepDto CreateStep() => new()
    {
        Id = "persist-workflow",
        Title = "Persist workflow",
        Description = "Persist the workflow and immutable revision.",
        Files = ["src/OneCode.App/Tools/CreatePlanTool.cs"],
        AcceptanceCriteria = ["Workflow restores after restart."],
        DependsOn = [],
        Risk = "Low",
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
