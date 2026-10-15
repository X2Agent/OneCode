// BuildRunCoordinatorTests partial：需求评估与状态转换守卫（原文件按职责区块的纯文本搬移）
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
namespace OneCode.Tests;

public sealed partial class BuildRunCoordinatorTests
{
    [Fact]
    public void RequirementAssessment_BroadProductRequest_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("Build a product-development and AI system.");

        result.RequiresClarification.Should().BeTrue();
    }

    [Fact]
    public void RequirementAssessment_ExplicitFixAndTest_DoesNotRequireClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("Fix the null reference in Foo.cs line 42 and run FooTests.");

        result.RequiresClarification.Should().BeFalse();
    }

    [Fact]
    public void RequirementAssessment_ModuleRefactorWithoutAcceptance_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("重构认证模块");

        result.GoalIsClear.Should().BeTrue();
        result.ScopeIsBounded.Should().BeTrue();
        result.AcceptanceIsDeterministic.Should().BeFalse();
        result.Risk.Should().Be(BuildRiskLevel.High);
        result.RequiresClarification.Should().BeTrue();
    }

    [Fact]
    public void RequirementAssessment_BoundedRefactorWithInvariantAndTests_DoesNotRequireClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("重构 AuthenticationService，保持公共 API 兼容，并运行认证测试验证行为不变。");

        result.GoalIsClear.Should().BeTrue();
        result.ScopeIsBounded.Should().BeTrue();
        result.AcceptanceIsDeterministic.Should().BeTrue();
        result.ConstraintsAreComplete.Should().BeTrue();
        result.RequiresUserDecision.Should().BeFalse();
        result.RequiresClarification.Should().BeFalse();
    }

    [Fact]
    public void RequirementAssessment_MultipleDomainsAndExternalDependencies_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("开发一个需求、研发、测试、发布全流程平台，接入数据库、消息队列和云部署。");

        result.ScopeIsBounded.Should().BeFalse();
        result.ConstraintsAreComplete.Should().BeFalse();
        result.RequiresUserDecision.Should().BeTrue();
        result.Risk.Should().Be(BuildRiskLevel.Medium);
        result.RequiresClarification.Should().BeTrue();
    }

    [Fact]
    public void Transition_ToCompleted_RejectsMissingValidationEvidence()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with { Validations = [] };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*final validation*");
    }

    [Fact]
    public void Transition_ToCompleted_RejectsTaskWithoutEvidence()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Plan = CreateAcceptingRun().Plan! with
            {
                Tasks = [new BuildPlanTask(
                    "t1",
                    "Fix",
                    "Fix",
                    [],
                    ["Foo.cs"],
                    ["a1"],
                    BuildTaskStatus.Completed)],
            },
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*evidence*");
    }

    [Fact]
    public void Transition_ToCompleted_RejectsEvidenceWithoutPersistentTaskCompletion()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Plan = CreateAcceptingRun().Plan! with
            {
                Tasks = [new BuildPlanTask(
                    "t1",
                    "Fix",
                    "Fix",
                    [],
                    ["Foo.cs"],
                    ["a1"],
                    BuildTaskStatus.Completed,
                    [new BuildTaskEvidence(BuildEvidenceKind.Validation, "v1", "Tests passed.")],
                    TaskItemId: "task-1")],
            },
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TaskCompletion*");
    }

    [Fact]
    public void Transition_ToCompleted_AllowsSkippedValidation()
    {
        // Skipped = 验证不适用（无匹配 profile，如生成 HTML 报告类工件），不是失败——
        // 与 MainAgentRunner 的 Skipped 语义一致，允许收口 Completed。
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Validations = [new BuildValidationRun(
                "v1",
                BuildValidationStatus.Skipped,
                [],
                ["No profile."],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().NotThrow();
    }
}








