using OneCode.App.Services.Agent;
using OneCode.Infrastructure.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.PlanMode;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class AgentContextPipelineTests
{
    [Fact]
    public async Task BuildForMain_BuildMode_DoesNotRegisterPlanAttachment()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Build);

        providers.Should().Contain(p => p is BuildModeAttachmentProvider);
        providers.Should().NotContain(p => p is PlanModeAttachmentProvider);
        providers.Should().NotContain(p => p is GoalContextProvider);
        providers.Should().Contain(p => p is ModeInstructionProvider);
    }

    [Fact]
    public async Task BuildForMain_PlanMode_DoesNotRegisterBuildAttachment()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Plan);

        providers.Should().Contain(p => p is PlanModeAttachmentProvider);
        providers.Should().NotContain(p => p is BuildModeAttachmentProvider);
        providers.Should().NotContain(p => p is GoalContextProvider);
    }

    [Fact]
    public async Task BuildForMain_GoalMode_RegistersGoalOnly()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = await pipeline.BuildForMainAsync(
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() },
            WorkingMode.Goal);

        providers.Should().Contain(p => p is GoalContextProvider);
        providers.Should().NotContain(p => p is BuildModeAttachmentProvider);
        providers.Should().NotContain(p => p is PlanModeAttachmentProvider);
    }

    [Fact]
    public void BuildShared_TeamMember_AppliesProfileDefaults()
    {
        var (shared, main) = TestAgentContextProviderAssembly.Create();
        var pipeline = new AgentContextPipeline(shared, main);

        var providers = pipeline.BuildShared(
            PipelineProfile.TeamMember,
            new AgentContextProviderOptions { WorkingDirectory = Path.GetTempPath() });

        providers.Should().NotBeEmpty();
        providers.Should().NotContain(p => p is ModeInstructionProvider);
    }
}
