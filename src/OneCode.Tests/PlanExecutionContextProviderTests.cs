using System.Reflection;
using Microsoft.Agents.AI;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services.Context;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Domain;
using OneCode.Core.Permissions;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

public sealed class PlanExecutionContextProviderTests
{
    private readonly IPlanWorkflowApplicationService _workflowService =
        Substitute.For<IPlanWorkflowApplicationService>();
    private readonly IPermissionModeProvider _modeProvider = Substitute.For<IPermissionModeProvider>();
    private readonly PlanExecutionContextProvider _sut;
    private readonly SessionId _sessionId = SessionId.NewId();

    public PlanExecutionContextProviderTests()
    {
        _modeProvider.CurrentMode.Returns(PermissionMode.AcceptEdits);
        _sut = new PlanExecutionContextProvider(_workflowService, _modeProvider);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ExecutingWorkflow_InjectsHandoffEveryTurn()
    {
        // 注入消息不进历史：压缩/恢复后模型历史里没有计划全文，所以每一轮都必须全量注入。
        _workflowService.GetAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(CreateWorkflow(PlanWorkflowState.Executing));
        ToolActivationContext.CurrentConversationId = _sessionId.ToString();
        try
        {
            var turn1 = await InvokeAsync(TestContext.Current.CancellationToken);
            var turn2 = await InvokeAsync(TestContext.Current.CancellationToken);

            foreach (var turn in new[] { turn1, turn2 })
            {
                var text = turn.Messages!.Single().Text;
                text.Should().Contain("build the thing", "用户原始诉求必须随计划一并交接");
                text.Should().Contain("Prefer sqlite", "规划期澄清必须随计划一并交接");
                text.Should().Contain("# Test plan", "批准计划全文必须在每轮上下文里");
            }
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Theory]
    [InlineData(PlanWorkflowState.AwaitingApproval)]
    [InlineData(PlanWorkflowState.Completed)]
    [InlineData(PlanWorkflowState.Failed)]
    public async Task ProvideAIContextAsync_WorkflowNotRunning_InjectsNothing(PlanWorkflowState state)
    {
        _workflowService.GetAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(CreateWorkflow(state));
        ToolActivationContext.CurrentConversationId = _sessionId.ToString();
        try
        {
            var result = await InvokeAsync(TestContext.Current.CancellationToken);

            result.Messages.Should().BeNull("只有 StartingExecution/Executing 才注入交接上下文");
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Fact]
    public async Task ProvideAIContextAsync_NotInAcceptEditsMode_ReturnsEmptyContext()
    {
        _modeProvider.CurrentMode.Returns(PermissionMode.Plan);

        var result = await InvokeAsync(TestContext.Current.CancellationToken);

        result.Messages.Should().BeNull();
        await _workflowService.DidNotReceive().GetAsync(
            Arg.Any<SessionId>(), Arg.Any<CancellationToken>());
    }

    private async Task<AIContext> InvokeAsync(CancellationToken ct)
    {
        // 反射调用 MAF 基类受保护的钩子：该 API 无公开形态，重命名会在此显式红——
        // 断言的是生产判定语义而非私有结构。
        var method = typeof(PlanExecutionContextProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (ValueTask<AIContext>)method.Invoke(_sut, new object?[] { null, ct })!;
        return await task;
    }

    private PlanWorkflow CreateWorkflow(PlanWorkflowState state)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlanWorkflow
        {
            Id = PlanWorkflowId.NewId(),
            SessionId = _sessionId,
            State = state,
            Version = 1,
            LatestRevision = 1,
            ApprovedSnapshot = new ApprovedPlanSnapshot
            {
                PlanId = PlanWorkflowId.NewId(),
                SessionId = _sessionId,
                Revision = 1,
                Title = "Test plan",
                Markdown = "# Test plan",
                Steps = [StepDefinition("s1"), StepDefinition("s2")],
                ContentHash = "sha256-test-plan",
                ApprovedBy = "user",
                ApprovedAt = now,
                OriginalRequest = "build the thing",
                Clarifications = ["Q&A: database? -> Prefer sqlite"],
            },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static PlanStepDefinition StepDefinition(string id) => new()
    {
        Id = id,
        Title = $"Step {id}",
        Description = "Do the work",
        Files = [],
        AcceptanceCriteria = [],
        DependsOn = [],
        Risk = PlanStepRisk.Low,
    };
}
