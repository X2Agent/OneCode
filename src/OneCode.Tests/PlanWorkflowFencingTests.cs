using OneCode.App.Services.PlanMode;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>
/// Plan 执行阶段 fenced 写切换的服务级行为锚：
/// claim 入口幂等、执行期写令牌匹配才落盘、错配/未 claim 带令牌 fail-closed、
/// 终态写（取消）沿用当前世代令牌、执行期事件携带令牌持久化。
/// </summary>
public sealed class PlanWorkflowFencingTests : IDisposable
{
    private const string RunId = "build-req-1";

    private readonly string _root;

    public PlanWorkflowFencingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "onecode-plan-fencing-svc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ClaimExecutionAsync_ClaimsOnce_TokenStableAcrossScans()
    {
        var (store, service, workflow) = await CreateExecutingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        claimed.WorkflowFencingToken.Should().NotBeNull();
        claimed.Version.Should().Be(workflow.Version + 1);

        // 幂等：恢复扫描重复进入同一执行世代不再递增令牌与版本。
        var reacquired = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: claimed.Version,
            TestContext.Current.CancellationToken);

        reacquired.WorkflowFencingToken.Should().Be(claimed.WorkflowFencingToken);
        reacquired.Version.Should().Be(claimed.Version);
        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.WorkflowFencingToken.Should().Be(claimed.WorkflowFencingToken);
    }

    [Fact]
    public async Task UpdateStepAsync_WithClaimedToken_PersistsFenced()
    {
        var (store, service, workflow) = await CreateExecutingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var result = await service.UpdateStepAsync(new UpdatePlanStepCommand(
            "update-1",
            workflow.SessionId,
            workflow.Id,
            RunId,
            "step-1",
            PlanStepExecutionStatus.InProgress,
            Evidence: null,
            Error: null,
            claimed.WorkflowFencingToken!.Value),
            TestContext.Current.CancellationToken);

        result.Workflow.StepExecutions.Single(step => step.StepId == "step-1").Status
            .Should().Be(PlanStepExecutionStatus.InProgress);
        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.StepExecutions.Single(step => step.StepId == "step-1").Status
            .Should().Be(PlanStepExecutionStatus.InProgress);
        reloaded.Workflow.WorkflowFencingToken.Should().Be(claimed.WorkflowFencingToken);
    }

    [Fact]
    public async Task UpdateStepAsync_WithoutToken_AfterClaim_FailsClosed()
    {
        var (store, service, workflow) = await CreateExecutingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var act = () => service.UpdateStepAsync(new UpdatePlanStepCommand(
            "update-no-token",
            workflow.SessionId,
            workflow.Id,
            RunId,
            "step-1",
            PlanStepExecutionStatus.InProgress,
            Evidence: null,
            Error: null),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanConcurrencyException>();
        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.Version.Should().Be(claimed.Version);
        reloaded.Workflow.StepExecutions.Single(step => step.StepId == "step-1").Status
            .Should().Be(PlanStepExecutionStatus.Pending);
    }

    [Fact]
    public async Task Unclaimed_ExecutionWrite_WithToken_FailsClosed()
    {
        var (_, service, workflow) = await CreateExecutingAsync();

        var act = () => service.UpdateStepAsync(new UpdatePlanStepCommand(
            "update-token-before-claim",
            workflow.SessionId,
            workflow.Id,
            RunId,
            "step-1",
            PlanStepExecutionStatus.InProgress,
            Evidence: null,
            Error: null,
            FencingToken: 12345),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanConcurrencyException>();
    }

    [Fact]
    public async Task StaleToken_AfterNewClaim_FailsClosed()
    {
        var (store, service, workflow) = await CreateExecutingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        // 新执行世代直接通过 store 以严格递增令牌重新 claim（如跨进程接管）。
        await store.ClaimWorkflowAsync(
            workflow.SessionId,
            workflow.Id,
            claimed.WorkflowFencingToken!.Value + 1,
            expectedVersion: claimed.Version,
            TestContext.Current.CancellationToken);

        var act = () => service.UpdateStepAsync(new UpdatePlanStepCommand(
            "update-stale",
            workflow.SessionId,
            workflow.Id,
            RunId,
            "step-1",
            PlanStepExecutionStatus.InProgress,
            Evidence: null,
            Error: null,
            claimed.WorkflowFencingToken.Value),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanConcurrencyException>();
    }

    [Fact]
    public async Task CancelAsync_AdoptsClaimedToken_PersistsDuringClaim()
    {
        var (store, service, workflow) = await CreateExecutingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var result = await service.CancelAsync(new CancelPlanCommand(
            "cancel-1",
            workflow.SessionId,
            workflow.Id,
            claimed.Version,
            "user cancelled during execution"),
            TestContext.Current.CancellationToken);

        result.Workflow.State.Should().Be(PlanWorkflowState.Cancelled);
        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.State.Should().Be(PlanWorkflowState.Cancelled);
        reloaded.Workflow.WorkflowFencingToken.Should().Be(claimed.WorkflowFencingToken);
    }

    [Fact]
    public async Task BuildRunStartedEvent_WithClaimedToken_TransitionsToExecuting()
    {
        var (store, service, workflow) = await CreateStartingAsync();
        var claimed = await service.ClaimExecutionAsync(
            workflow.SessionId, workflow.Id, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        await service.HandleRunEventAsync(new BuildRunStartedEvent(
            workflow.SessionId,
            workflow.Id,
            $"build-{workflow.ExecutionRequestId}",
            DateTimeOffset.UtcNow,
            claimed.WorkflowFencingToken!.Value),
            TestContext.Current.CancellationToken);

        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.State.Should().Be(PlanWorkflowState.Executing);
        reloaded.Workflow.WorkflowFencingToken.Should().Be(claimed.WorkflowFencingToken);
    }

    private async Task<(PlanAggregateStore Store, PlanWorkflowApplicationService Service, PlanWorkflow Workflow)>
        CreateExecutingAsync()
    {
        var store = new PlanAggregateStore(_root);
        var service = new PlanWorkflowApplicationService(store);
        var workflow = PlanWorkflow.Create(SessionId.NewId()) with
        {
            State = PlanWorkflowState.Executing,
            Version = 3,
            LatestRevision = 1,
            SubmittedRevision = 1,
            ApprovedRevision = 1,
            ExecutionRequestId = "req-1",
            ActiveRunId = RunId,
            ActiveRunKind = PlanRunKind.Build,
            ApprovedSnapshot = CreateSnapshot(),
            StepExecutions =
            [
                new PlanStepExecution
                {
                    StepId = "step-1",
                    Status = PlanStepExecutionStatus.Pending,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var aggregate = new PlanAggregate(workflow, [CreateRevision(workflow)]);
        await store.SaveAsync(aggregate, expectedVersion: -1, TestContext.Current.CancellationToken);
        return (store, service, workflow);
    }

    private async Task<(PlanAggregateStore Store, PlanWorkflowApplicationService Service, PlanWorkflow Workflow)>
        CreateStartingAsync()
    {
        var store = new PlanAggregateStore(_root);
        var service = new PlanWorkflowApplicationService(store);
        var workflow = PlanWorkflow.Create(SessionId.NewId()) with
        {
            State = PlanWorkflowState.StartingExecution,
            Version = 3,
            LatestRevision = 1,
            SubmittedRevision = 1,
            ApprovedRevision = 1,
            ExecutionRequestId = "req-1",
            ActiveRunId = null,
            ActiveRunKind = PlanRunKind.Build,
            ApprovedSnapshot = CreateSnapshot(),
            StepExecutions =
            [
                new PlanStepExecution
                {
                    StepId = "step-1",
                    Status = PlanStepExecutionStatus.Pending,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var aggregate = new PlanAggregate(workflow, [CreateRevision(workflow)]);
        await store.SaveAsync(aggregate, expectedVersion: -1, TestContext.Current.CancellationToken);
        return (store, service, workflow);
    }

    private static ApprovedPlanSnapshot CreateSnapshot()
        => new()
        {
            PlanId = PlanWorkflowId.NewId(),
            SessionId = SessionId.NewId(),
            Revision = 1,
            Title = "Test plan",
            Markdown = "# Test plan",
            Steps =
            [
                new PlanStepDefinition
                {
                    Id = "step-1",
                    Title = "Step 1",
                    Description = "Do step 1",
                    DependsOn = [],
                    Files = [],
                    AcceptanceCriteria = [],
                    Risk = PlanStepRisk.Low,
                },
            ],
            ContentHash = "sha256-test",
            ApprovedBy = "user",
            ApprovedAt = DateTimeOffset.UtcNow,
        };

    private static PlanRevision CreateRevision(PlanWorkflow workflow)
        => new()
        {
            PlanId = workflow.Id,
            SessionId = workflow.SessionId,
            Revision = 1,
            Title = "Test plan",
            Markdown = "# Test plan",
            Steps = [],
            Risks = [],
            Assumptions = [],
            ContentHash = "sha256-test",
            Status = PlanRevisionStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}