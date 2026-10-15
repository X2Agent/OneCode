// BuildRunCoordinatorTests partial：计划审批与驳回守卫（state 门、空策略门、工作区漂移重基线门）。
using NSubstitute;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Workflows;

namespace OneCode.Tests;

public sealed partial class BuildRunCoordinatorTests
{
    [Fact]
    public async Task ApprovePlanAsync_RejectsNonPlannedState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);
        run = await ApprovePlanAsync(sut, run);
        run.State.Should().Be(BuildRunState.Implementing);

        // 同一审批源重放：若 state 门被破坏，幂等分支（同源直接返回 current）会放行，
        // 测试随之变红——这是本用例对"state 门"而非"幂等门"的反证构造。
        var reapprove = () => sut.ApprovePlanAsync(
            run.Id,
            new ApprovedToolPolicy(["ReadFile", "Write"]),
            "test",
            TestContext.Current.CancellationToken);

        await reapprove.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot approve a plan from state*");
    }

    [Fact]
    public async Task ApprovePlanAsync_RejectsEmptyPolicy()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        var approve = () => sut.ApprovePlanAsync(
            run.Id,
            new ApprovedToolPolicy([]),
            "test",
            TestContext.Current.CancellationToken);

        await approve.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*at least one tool*");
        (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!
            .State.Should().Be(BuildRunState.Planned,
                "非法策略必须被拒绝且不产生任何状态推进");
    }

    [Fact]
    public async Task ApprovePlanAsync_WorkspaceDrift_BlocksInsteadOfExecuting()
    {
        // 计划生成与用户审批之间工作区必须无漂移：审批时重算指纹，与计划期不一致
        // 即 Blocked（要求重新基线），绝不在漂移的工作区上直接开跑。
        var store = new InMemoryBuildRunStore();
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("fingerprint-a"), // 计划期快照
                Task.FromResult("fingerprint-b")); // 审批期重算 → 漂移
        var sut = CreateCoordinator(store, provider);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        var blocked = await sut.ApprovePlanAsync(
            run.Id,
            new ApprovedToolPolicy(["ReadFile", "Write", "Edit", "Bash"]),
            "test",
            TestContext.Current.CancellationToken);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.TerminalReason.Should().Be(RunTerminalReason.Blocked);
        blocked.PlanRejectionReason.Should().Be("workspace-drift");
        blocked.FailureSummary.Should().Contain("Workspace changed");
        blocked.ApprovedToolPolicy.Should().BeNull("漂移的工作区不得进入执行");
    }

    [Fact]
    public async Task RejectPlanAsync_BlocksWithReason()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        var blocked = await sut.RejectPlanAsync(
            run.Id,
            "scope is too broad",
            TestContext.Current.CancellationToken);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.TerminalReason.Should().Be(RunTerminalReason.Blocked);
        blocked.PlanRejectionReason.Should().Be("scope is too broad");
        blocked.FailureSummary.Should().Contain("scope is too broad");
        blocked.ApprovedToolPolicy.Should().BeNull();
    }

    [Fact]
    public async Task RejectPlanAsync_EmptyReason_IsRejected()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        var reject = () => sut.RejectPlanAsync(
            run.Id,
            "   ",
            TestContext.Current.CancellationToken);

        await reject.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Rejection reason is required*");
    }

    [Fact]
    public async Task RejectPlanAsync_RejectsNonPlannedState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);
        await sut.RejectPlanAsync(run.Id, "first rejection", TestContext.Current.CancellationToken);

        var rereject = () => sut.RejectPlanAsync(
            run.Id,
            "second rejection",
            TestContext.Current.CancellationToken);

        await rereject.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot reject a plan from state*");
    }
}