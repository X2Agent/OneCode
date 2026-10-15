using OneCode.Core.Agent;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 只读子代理 profile 的 CodeAct 边界：Explore / Plan 角色本就只读，
/// 广告 execute_code 只会换来一轮「审批后拒绝」的浪费往返。
/// </summary>
public sealed class PipelineProfileCodeActBoundaryTests
{
    [Theory]
    [InlineData(PipelineProfile.Explore)]
    [InlineData(PipelineProfile.Plan)]
    public void For_ReadOnlyProfiles_ExcludeCodeAct(PipelineProfile profile)
    {
        PipelineProfileBehavior.For(profile).Has(AgentCapability.CodeAct).Should().BeFalse(
            "read-only profiles must not carry execute_code into the model's tool list");
    }

    [Fact]
    public void For_FullProfile_KeepsCodeAct()
    {
        PipelineProfileBehavior.For(PipelineProfile.Full).Has(AgentCapability.CodeAct).Should().BeTrue(
            "the interactive main session keeps the sandboxed code interpreter");
    }
}