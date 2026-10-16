using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Build;
using OneCode.Core.Hooks;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

/// <summary>
/// 主代理管道装配：Plan 阶段挂载提交封条探针，计划执行 run 开启待办停滞提醒。
/// </summary>
public sealed class AgentPipelineAssemblyTests
{
    private static AgentPipelineOptions Build(MainAgentRunOptions options)
    {
        var assembly = new AgentPipelineAssembly(
            workingDirectoryAccessor: Substitute.For<IWorkingDirectoryAccessor>(),
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            verificationProvider: Substitute.For<IVerificationProvider>(),
            modeProvider: Substitute.For<IPermissionModeProvider>(),
            permissionChecker: Substitute.For<IPermissionChecker>(),
            tokenLedger: new TokenLedger());
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
        return assembly.BuildMainOptions(options, transaction, @"C:\repo", "m", providerId: null);
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
}
