using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Coordinator;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

/// <summary>
/// ParallelDag 模式：模板解析、扇出计划生成（独立分支 + 聚合任务）。
/// </summary>
public sealed class ParallelDagTeamTests
{
    [Fact]
    public async Task RegisterTeamAsync_ParallelDagTemplate_DetectsMode()
    {
        var tempDir = MafTeamOrchestrationTests.CreateTempDir();
        try
        {
            var teamDir = Path.Combine(tempDir, "fanout");
            Directory.CreateDirectory(teamDir);
            var yaml = """
                name: fanout
                template: parallel-dag
                workers:
                  - name: a
                    role: security
                  - name: b
                    role: performance
                  - name: c
                    role: maintainability
                """;
            var yamlPath = Path.Combine(teamDir, "team.yaml");
            await File.WriteAllTextAsync(yamlPath, yaml, TestContext.Current.CancellationToken);

            var sut = MafTeamOrchestrationTests.CreateSut();
            await sut.RegisterTeamAsync("fanout", yamlPath, TestContext.Current.CancellationToken);

            sut.GetTeamMode("fanout").Should().Be(TeamOrchestrationMode.ParallelDag);
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task CreateImplementationPlan_ParallelDag_GeneratesBranchesWithAggregate()
    {
        var requirementService = CreateRequirementService();
        var goal = "审查这段代码 Clarification response:\n1. ok";
        var analysis = await requirementService.AnalyzeAsync(goal, TestContext.Current.CancellationToken);
        analysis.CanProceedWithoutClarification.Should().BeTrue();

        var config = new TeamConfig(
            "fanout",
            "(builtin)",
            [
                new TeamMember("fanout-security", "security", null),
                new TeamMember("fanout-performance", "performance", null),
                new TeamMember("fanout-maintainability", "maintainability", null),
            ],
            MaxTurns: 15,
            Mode: TeamOrchestrationMode.ParallelDag);

        var plan = requirementService.CreateImplementationPlan(analysis, config);

        plan.Tasks.Count.Should().Be(4); // 3 分支 + 1 聚合
        var branches = plan.Tasks.Where(t => t.Id.StartsWith("branch-", StringComparison.Ordinal)).ToList();
        branches.Should().HaveCount(3);
        branches.Should().OnlyContain(t => t.DependsOn.Count == 0, "branches must be independent for parallel fan-out");
        branches.Should().OnlyContain(t => t.ToolPolicy == TeamToolPolicy.ReadOnly);

        var aggregate = plan.Tasks.Single(t => t.Id == "aggregate");
        aggregate.DependsOn.Should().BeEquivalentTo(branches.Select(t => t.Id),
            "aggregate must depend on every branch");
    }


    private static TeamRequirementService CreateRequirementService()
    {
        return new TeamRequirementService(
            new RequirementAssessmentService(),
            Substitute.For<IClarificationQuestionGenerator>());
    }

    private static void SafeDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
