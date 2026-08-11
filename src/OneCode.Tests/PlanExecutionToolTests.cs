using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Tools;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

public sealed class PlanExecutionToolTests
{
    [Fact]
    public async Task UpdatePlanStepAsync_UpdatesMappedBuildTaskAndPersistsEvidence()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        const string stepId = "implementation";
        var workflow = CreateWorkflow(sessionId, runId, stepId);
        var updatedWorkflow = workflow with
        {
            Version = workflow.Version + 1,
            StepExecutions = [workflow.StepExecutions[0] with
            {
                Status = PlanStepExecutionStatus.Completed,
                Evidence = "Foo.cs changed",
            }],
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(updatedWorkflow));
        var tasks = new TaskService();
        var task = tasks.CreateTask(
            "Implement",
            "Fix Foo.cs",
            status: TaskStatus.InProgress,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = stepId,
                }),
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);
        var sut = new PlanExecutionTool(
            workflowService,
            new PlanCardPublisher(),
            tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            var result = await sut.UpdatePlanStepAsync(
                stepId,
                "completed",
                "Foo.cs changed",
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            tasks.GetTask(task.Id)!.Status.Should().Be(TaskStatus.Completed);
            tasks.GetTaskOutput(task.Id).Should().Contain("Foo.cs changed");
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
            OneCodeAgentRunContext.CurrentRunId = null;
            OneCodeAgentRunContext.CurrentBuildRunId = null;
        }
    }

    [Fact]
    public async Task UpdatePlanStepAsync_BlockedDependency_DoesNotMutateWorkflow()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        var workflow = CreateWorkflow(sessionId, runId, "verification");
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        var tasks = new TaskService();
        var dependency = tasks.CreateTask(
            "Implementation",
            "Implement",
            status: TaskStatus.InProgress,
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);
        _ = tasks.CreateTask(
            "Verification",
            "Verify",
            status: TaskStatus.Pending,
            blockedBy: [dependency.Id],
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = "verification",
                }),
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);
        var sut = new PlanExecutionTool(
            workflowService,
            new PlanCardPublisher(),
            tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            var result = await sut.UpdatePlanStepAsync(
                "verification",
                "in_progress",
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("blocked");
            await workflowService.DidNotReceive().UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
            OneCodeAgentRunContext.CurrentRunId = null;
            OneCodeAgentRunContext.CurrentBuildRunId = null;
        }
    }

    [Fact]
    public async Task UpdatePlanStepAsync_ReconcilesPersistedWorkflowProjectionBeforeNextMutation()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        const string stepId = "implementation";
        var workflow = CreateWorkflow(sessionId, runId, stepId) with
        {
            StepExecutions = [new PlanStepExecution
            {
                StepId = stepId,
                Status = PlanStepExecutionStatus.Completed,
                Evidence = "persisted evidence",
                UpdatedAt = DateTimeOffset.UtcNow,
            }],
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(workflow));
        var tasks = new TaskService();
        var task = tasks.CreateTask(
            "Implement",
            "Fix Foo.cs",
            status: TaskStatus.InProgress,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = stepId,
                }),
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);
        var sut = new PlanExecutionTool(workflowService, new PlanCardPublisher(), tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            var result = await sut.UpdatePlanStepAsync(
                stepId,
                "completed",
                "persisted evidence",
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            tasks.GetTask(task.Id)!.Status.Should().Be(TaskStatus.Completed);
            tasks.GetTaskOutput(task.Id).Should().Contain("persisted evidence");
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
            OneCodeAgentRunContext.CurrentRunId = null;
            OneCodeAgentRunContext.CurrentBuildRunId = null;
        }
    }

    [Fact]
    public async Task UpdatePlanStepAsync_ReplayedProjection_DoesNotDuplicateEvidence()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        const string stepId = "implementation";
        var workflow = CreateWorkflow(sessionId, runId, stepId);
        var updatedWorkflow = workflow with
        {
            Version = 2,
            StepExecutions = [workflow.StepExecutions[0] with
            {
                Status = PlanStepExecutionStatus.Completed,
                Evidence = "Foo.cs changed",
            }],
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(updatedWorkflow));
        var tasks = new TaskService();
        var task = tasks.CreateTask(
            "Implement",
            "Fix Foo.cs",
            status: TaskStatus.InProgress,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = stepId,
                }),
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);
        var sut = new PlanExecutionTool(workflowService, new PlanCardPublisher(), tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            await sut.UpdatePlanStepAsync(stepId, "completed", "Foo.cs changed", ct: TestContext.Current.CancellationToken);
            await sut.UpdatePlanStepAsync(stepId, "completed", "Foo.cs changed", ct: TestContext.Current.CancellationToken);

            tasks.GetTaskOutput(task.Id).Split("Foo.cs changed").Length.Should().Be(2);
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
            OneCodeAgentRunContext.CurrentRunId = null;
            OneCodeAgentRunContext.CurrentBuildRunId = null;
        }
    }

    private static PlanWorkflow CreateWorkflow(
        SessionId sessionId,
        string runId,
        string stepId)
    {
        var now = DateTimeOffset.UtcNow;
        var created = PlanWorkflow.Create(sessionId);
        return created with
        {
            State = PlanWorkflowState.Executing,
            Version = 1,
            ActiveRunId = runId,
            ActiveRunKind = PlanRunKind.Build,
            ApprovedSnapshot = new ApprovedPlanSnapshot
            {
                PlanId = created.Id,
                SessionId = sessionId,
                Revision = 1,
                Markdown = "# Approved plan",
                Steps = [new PlanStepDefinition
                {
                    Id = stepId,
                    Title = stepId,
                    Description = stepId,
                    Files = [],
                    AcceptanceCriteria = ["done"],
                    DependsOn = [],
                    Risk = PlanStepRisk.Low,
                }],
                ContentHash = "sha256-test",
                ApprovedBy = "user",
                ApprovedAt = now,
            },
            StepExecutions = [new PlanStepExecution
            {
                StepId = stepId,
                Status = PlanStepExecutionStatus.InProgress,
                UpdatedAt = now,
            }],
            UpdatedAt = now,
        };
    }
}
