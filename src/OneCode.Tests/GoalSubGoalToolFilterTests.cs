using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;

namespace OneCode.Tests;

/// <summary>
/// GOAL 子目标工具面守卫（P11）：所有路径无条件摘除派工工具（Agent / ParallelAgents），
/// 与 fork 路径「嵌套派工深度上限 1」语义一致；摘除后空集 fail-closed，不静默零工具。
/// </summary>
public sealed class GoalSubGoalToolFilterTests
{
    private static IList<AITool> Tools(params string[] names)
        => names.Select(name => (AITool)AIFunctionFactory.Create(() => { }, name: name)).ToList();

    private static readonly string[] AllToolNames = ["Read", "Write", "Bash", "Agent", "ParallelAgents"];

    [Theory]
    [InlineData(0)] // null
    [InlineData(1)] // 空列表
    [InlineData(2)] // "*"
    public void UnrestrictedPaths_StripsDispatchTools(int mode)
    {
        var requiredTools = mode switch
        {
            0 => (IReadOnlyList<string>?)null,
            1 => [],
            _ => ["*"],
        };

        var filtered = GoalSubGoalExecutor.FilterToolsForSubGoal(Tools([.. AllToolNames]), requiredTools);

        // 派工工具（Agent/ParallelAgents）摘除，其余保留
        filtered.Select(t => ((AIFunction)t).Name).Should().BeEquivalentTo("Read", "Write", "Bash");
    }

    [Fact]
    public void ExplicitRequiredTools_StripsDispatchToolsFromMatch()
    {
        var filtered = GoalSubGoalExecutor.FilterToolsForSubGoal(
            Tools([.. AllToolNames]), ["Read", "Agent"]);

        filtered.Select(t => ((AIFunction)t).Name).Should().BeEquivalentTo("Read");
    }

    [Fact]
    public void DispatchOnlyRequiredTools_FailsClosed()
    {
        // 反证：分解器把派工工具写进 RequiredTools——摘除后空集必须抛错，不得静默零工具。
        var act = () => GoalSubGoalExecutor.FilterToolsForSubGoal(
            Tools([.. AllToolNames]), ["Agent", "ParallelAgents"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dispatch tools*nested dispatch depth limit 1*");
    }

    [Fact]
    public void MixedUnknownAndValidNames_KeepsValidMatches()
    {
        // 既有契约：fail-closed 只针对「零匹配」；部分未知名被静默丢弃，合法匹配保留。
        var filtered = GoalSubGoalExecutor.FilterToolsForSubGoal(
            Tools([.. AllToolNames]), ["Read", "NonexistentTool"]);

        filtered.Select(t => ((AIFunction)t).Name).Should().BeEquivalentTo("Read");
    }
}
