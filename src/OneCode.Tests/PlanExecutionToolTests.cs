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
        workflowService.CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(updatedWorkflow with
            {
                State = PlanWorkflowState.Verifying,
                Version = updatedWorkflow.Version + 1,
            }));
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
        workflowService.CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(workflow with
            {
                State = PlanWorkflowState.Verifying,
                Version = workflow.Version + 1,
            }));
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
        workflowService.CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(updatedWorkflow with
            {
                State = PlanWorkflowState.Verifying,
                Version = updatedWorkflow.Version + 1,
            }));
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

    private static TaskItem CreateMappedTask(
        TaskService tasks,
        SessionId sessionId,
        string buildRunId,
        string stepId)
        => tasks.CreateTask(
            stepId,
            stepId,
            status: TaskStatus.InProgress,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = stepId,
                }),
            conversationId: sessionId.ToString(),
            buildRunId: buildRunId);

    [Fact]
    public async Task UpdatePlanStepAsync_LastTerminalStep_AutoDerivesVerificationTransition()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        var workflow = CreateWorkflow(sessionId, runId, "implementation", "verification");
        var verifyingWorkflow = workflow with
        {
            State = PlanWorkflowState.Verifying,
            Version = 2,
            StepExecutions =
            [
                workflow.StepExecutions[0] with
                {
                    Status = PlanStepExecutionStatus.Completed,
                    Evidence = "impl evidence",
                },
                workflow.StepExecutions[1] with
                {
                    Status = PlanStepExecutionStatus.Completed,
                    Evidence = "test evidence",
                },
            ],
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(verifyingWorkflow with
            {
                State = PlanWorkflowState.Executing,
                Version = 1,
            }));
        workflowService.CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(verifyingWorkflow));
        var tasks = new TaskService();
        CreateMappedTask(tasks, sessionId, buildRunId, "implementation");
        var verificationTask = CreateMappedTask(tasks, sessionId, buildRunId, "verification");
        var sut = new PlanExecutionTool(workflowService, new PlanCardPublisher(), tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            var result = await sut.UpdatePlanStepAsync(
                "verification",
                "completed",
                "test evidence",
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            result.Content.Should().Contain("verification_required");
            await workflowService.Received(1).CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
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
    public async Task UpdatePlanStepAsync_StepsStillIncomplete_DoesNotAutoDerive()
    {
        var sessionId = SessionId.NewId();
        const string runId = "approved-build-run";
        const string buildRunId = "br-test";
        var workflow = CreateWorkflow(sessionId, runId, "implementation", "verification");
        var updatedWorkflow = workflow with
        {
            Version = 2,
            StepExecutions =
            [
                workflow.StepExecutions[0] with
                {
                    Status = PlanStepExecutionStatus.Completed,
                    Evidence = "impl evidence",
                },
                workflow.StepExecutions[1],
            ],
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.UpdateStepAsync(
                Arg.Any<UpdatePlanStepCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(updatedWorkflow));
        var tasks = new TaskService();
        CreateMappedTask(tasks, sessionId, buildRunId, "implementation");
        CreateMappedTask(tasks, sessionId, buildRunId, "verification");
        var sut = new PlanExecutionTool(workflowService, new PlanCardPublisher(), tasks);
        ToolActivationContext.CurrentConversationId = sessionId.ToString();
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        try
        {
            var result = await sut.UpdatePlanStepAsync(
                "implementation",
                "completed",
                "impl evidence",
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            result.Content.Should().Contain("step_updated");
            await workflowService.DidNotReceive().CompleteExecutionAsync(
                Arg.Any<CompletePlanExecutionCommand>(),
                Arg.Any<CancellationToken>());
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
        params string[] stepIds)
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
                Steps = stepIds.Select(stepId => new PlanStepDefinition
                {
                    Id = stepId,
                    Title = stepId,
                    Description = stepId,
                    Files = [],
                    AcceptanceCriteria = ["done"],
                    DependsOn = [],
                    Risk = PlanStepRisk.Low,
                }).ToArray(),
                ContentHash = "sha256-test",
                ApprovedBy = "user",
                ApprovedAt = now,
            },
            StepExecutions = stepIds.Select(stepId => new PlanStepExecution
            {
                StepId = stepId,
                Status = PlanStepExecutionStatus.InProgress,
                UpdatedAt = now,
            }).ToArray(),
            UpdatedAt = now,
        };
    }
}
