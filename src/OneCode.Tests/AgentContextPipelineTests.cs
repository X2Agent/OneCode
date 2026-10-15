using OneCode.App.Services.Agent;
using OneCode.App.Services.Context;
using OneCode.App.Services.GoalMode;
using OneCode.Infrastructure.Agent;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class AgentContextPipelineTests
{
    [Fact]
    public async Task BuildForMain_BuildMode_RegistersModeInstructionAndPlanExecutionWithoutGoal()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Build);

        providers.Should().Contain(p => p is ModeInstructionProvider);
        providers.Should().Contain(p => p is PlanExecutionContextProvider);
        providers.Should().NotContain(p => p is GoalContextProvider);
    }

    [Fact]
    public async Task BuildForMain_PlanMode_RegistersModeInstructionAndPlanExecutionWithoutGoal()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Plan);

        providers.Should().Contain(p => p is ModeInstructionProvider);
        providers.Should().Contain(p => p is PlanExecutionContextProvider);
        providers.Should().NotContain(p => p is GoalContextProvider);
    }

    [Fact]
    public async Task BuildForMain_GoalMode_RegistersGoalWithoutPlanExecution()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Goal);

        providers.Should().Contain(p => p is ModeInstructionProvider);
        providers.Should().Contain(p => p is GoalContextProvider);
        providers.Should().NotContain(p => p is PlanExecutionContextProvider);
    }

    [Fact]
    public void BuildShared_Worker_AppliesProfileDefaults()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = pipeline.BuildShared(
            PipelineProfile.Worker,
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() });

        providers.Should().NotBeEmpty();
        providers.Should().NotContain(p => p is ModeInstructionProvider);
    }
}
