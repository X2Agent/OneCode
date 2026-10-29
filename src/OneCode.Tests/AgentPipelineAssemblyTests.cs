using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Build;
using OneCode.Core.Hooks;
using OneCode.Core.Permissions;
using OneCode.Core.Tokens;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

/// <summary>
/// 主代理管道装配：Plan 阶段挂载提交封条探针，计划执行 run 开启待办停滞提醒。
/// </summary>
public sealed class AgentPipelineAssemblyTests
{
    private static AgentPipelineOptions Build(
        MainAgentRunOptions options,
        TokenLedger? ledger = null,
        long? budgetBaselineTokens = null)
    {
        var assembly = new AgentPipelineAssembly(
            workingDirectoryAccessor: Substitute.For<IWorkingDirectoryAccessor>(),
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            verificationProvider: Substitute.For<IVerificationProvider>(),
            modeProvider: Substitute.For<IPermissionModeProvider>(),
            permissionChecker: Substitute.For<IPermissionChecker>(),
            tokenLedger: ledger ?? new TokenLedger());
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
        return assembly.BuildMainOptions(
            options, transaction, @"C:\repo", "m", providerId: null, budgetBaselineTokens);
    }

    [Fact]
    public void BuildMainOptions_PlanMode_MountsRunSealBoundToPlanSubmissionSeal()
    {
        var built = Build(new MainAgentRunOptions { ModelId = "m", WorkingMode = WorkingMode.Plan });

        built.IsRunSealed.Should().NotBeNull("Plan runs must check the submission seal before executing any tool");
        try
        {
            var seal = new RunSeal();
            OneCodeAgentRunContext.PlanSubmissionSeal = seal;
            built.IsRunSealed!().Should().BeFalse();

            seal.Seal();
            built.IsRunSealed!().Should().BeTrue("sealing after SubmitPlan must immediately close the run to tools");
        }
        finally
        {
            OneCodeAgentRunContext.PlanSubmissionSeal = null;
        }
    }

    [Theory]
    [InlineData(WorkingMode.Build)]
    [InlineData(WorkingMode.Goal)]
    public void BuildMainOptions_NonPlanMode_DoesNotMountPlanSubmissionSeal(WorkingMode mode)
    {
        Build(new MainAgentRunOptions { ModelId = "m", WorkingMode = mode }).IsRunSealed.Should().BeNull();
    }

    [Fact]
    public void BuildMainOptions_SeededPlanRun_EnablesTodoStallNudge()
    {
        var seeded = Build(new MainAgentRunOptions
        {
            ModelId = "m",
            WorkingMode = WorkingMode.Build,
            SeedTodoTasks = [new BuildPlanTask("s1", "Collect", "Collect sources", [], [], ["done"])],
        });
        var plain = Build(new MainAgentRunOptions { ModelId = "m", WorkingMode = WorkingMode.Build });

        seeded.EnableTodoStallNudge.Should().BeTrue("seeded todos are the step progress, so stalls must be nudged");
        plain.EnableTodoStallNudge.Should().BeFalse("ordinary runs keep the model's own pacing");
    }

    [Fact]
    public void BuildMainOptions_MountsLiveWorkingDirectoryProvider_FromSessionAccessor()
    {
        // 反证锚点：不挂活动目录提供者时，同 run 内 EnterWorktree 切换会话目录后，
        // 权限边界/EditTransaction/FileSystemInvariant 仍按 run 起始目录判定（失配缺陷）。
        var accessor = Substitute.For<IWorkingDirectoryAccessor>();
        var liveDir = Path.Combine(Path.GetTempPath(), "repo");
        accessor.WorkingDirectory.Returns(liveDir);
        var assembly = new AgentPipelineAssembly(
            workingDirectoryAccessor: accessor,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            verificationProvider: Substitute.For<IVerificationProvider>(),
            modeProvider: Substitute.For<IPermissionModeProvider>(),
            permissionChecker: Substitute.For<IPermissionChecker>(),
            tokenLedger: new TokenLedger());
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);

        var built = assembly.BuildMainOptions(
            new MainAgentRunOptions { ModelId = "m", WorkingMode = WorkingMode.Build },
            transaction,
            @"C:\repo",
            "m",
            providerId: null);

        built.WorkingDirectoryProvider.Should().NotBeNull("主路径必须挂活动目录提供者");
        built.ResolveWorkingDirectory().Should().Be(liveDir,
            "中间件每次调用解析到的必须是会话当前目录，而非 run 起始快照");
    }

    [Fact]
    public void AgentPipelineOptions_NoProvider_FallsBackToFixedWorkingDirectory()
    {
        // fork/子代理路径不挂提供者：回退固定值语义保持不变。
        var options = new AgentPipelineOptions { WorkingDirectory = @"C:\fixed" };

        options.WorkingDirectoryProvider.Should().BeNull();
        options.ResolveWorkingDirectory().Should().Be(@"C:\fixed");
    }

    [Fact]
    public void BuildMainOptions_GoalAutoWithoutBroker_DisablesToolApprovalToPreventLoopTermination()
    {
        var modeProvider = Substitute.For<IPermissionModeProvider>();
        modeProvider.CurrentMode.Returns(PermissionMode.GoalAuto);
        var assembly = new AgentPipelineAssembly(
            workingDirectoryAccessor: Substitute.For<IWorkingDirectoryAccessor>(),
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            verificationProvider: Substitute.For<IVerificationProvider>(),
            modeProvider: modeProvider,
            permissionChecker: Substitute.For<IPermissionChecker>(),
            tokenLedger: new TokenLedger());
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);

        var built = assembly.BuildMainOptions(
            new MainAgentRunOptions
            {
                ModelId = "m",
                WorkingMode = WorkingMode.Goal,
                ApprovalBroker = null,
                SuppressToolApproval = true,
            },
            transaction,
            @"C:\repo",
            "m",
            providerId: null);

        built.EnableToolApproval.Should().BeFalse(
            "autonomous Goal sub-goals have no interactive approval broker; wrapping tools in ApprovalRequiredAIFunction would abort LoopAgent on any denied path");
    }

    [Fact]
    public void BuildMainOptions_HeadlessPlan_UsesCurrentGlobalUsageAsBaseline()
    {
        var ledger = new TokenLedger();
        ledger.RecordUsage(new UsageRecord("m", 100, 50));

        var built = Build(
            new MainAgentRunOptions { ModelId = "m", WorkingMode = WorkingMode.Plan },
            ledger);

        built.ConversationId.Should().BeNull();
        built.BudgetBaselineTokens.Should().Be(150,
            "无会话计量域按 run 增量计算，历史全局用量必须进入基线而不是立即触发熔断");
    }

    [Theory]
    [InlineData(WorkingMode.Plan)]
    [InlineData(WorkingMode.Goal)]
    public void BuildMainOptions_SessionPlanOrGoal_KeepsSessionCumulativeBaseline(WorkingMode mode)
    {
        var sessionId = SessionId.NewId();
        var ledger = new TokenLedger();
        ledger.RecordUsage(sessionId, new UsageRecord("m", 1000, 200, 500, 50));

        var built = Build(
            new MainAgentRunOptions
            {
                ModelId = "m",
                WorkingMode = mode,
                ConversationId = sessionId,
            },
            ledger);

        built.ConversationId.Should().Be(sessionId);
        built.BudgetBaselineTokens.Should().Be(0, "PLAN/GOAL 仍按整个会话桶累计，而不是按 run 增量");
    }

    [Fact]
    public void BuildMainOptions_SessionBuild_UsesSessionBucketSnapshotAsBaseline()
    {
        var sessionId = SessionId.NewId();
        var ledger = new TokenLedger();
        ledger.RecordUsage(sessionId, new UsageRecord("m", 1000, 200, 500, 50));
        ledger.RecordUsage(SessionId.NewId(), new UsageRecord("m", 5000, 1000));

        var built = Build(
            new MainAgentRunOptions
            {
                ModelId = "m",
                WorkingMode = WorkingMode.Build,
                ConversationId = sessionId,
            },
            ledger);

        built.BudgetBaselineTokens.Should().Be(800,
            "BUILD 只按当前会话桶的 run 增量计量，不能混入其他会话的全局用量");
    }

    [Fact]
    public void BuildMainOptions_ExplicitBaseline_OverridesEveryScope()
    {
        var ledger = new TokenLedger();
        ledger.RecordUsage(new UsageRecord("m", 100, 50));

        var built = Build(
            new MainAgentRunOptions { ModelId = "m", WorkingMode = WorkingMode.Plan },
            ledger,
            budgetBaselineTokens: 123);

        built.BudgetBaselineTokens.Should().Be(123, "零输出重试等调用方显式传入的基线必须优先");
    }
}
