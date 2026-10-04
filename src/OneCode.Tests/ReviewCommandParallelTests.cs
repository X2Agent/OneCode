using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Lsp;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// /review 三视角并行守卫：视角派发数量恒为 3、全部只读工具边界（无 Bash/Edit/Write）、
/// 视角 prompt 隔离、聚合任务等三视角 barrier 汇聚并接收全部上游输出（去重的输入前提）。
/// </summary>
public sealed class ReviewCommandParallelTests
{
    private static IReadOnlyList<AgentWorkflowTask> BuildDefaultTasks()
        => ReviewCommand.BuildPerspectiveTasks(
            ReviewCommand.ParallelPerspectives.ToDictionary(p => p.PromptName, p => $"{p.Key}-PROMPT"),
            "AGGREGATE-PROMPT");

    [Fact]
    public void BuildPerspectiveTasks_DispatchesExactlyThreeIsolatedPerspectives()
    {
        var tasks = BuildDefaultTasks();

        tasks.Should().HaveCount(ReviewCommand.ParallelPerspectives.Length + 1);
        tasks.Count(t => t.Id != ReviewCommand.AggregateTaskId).Should().Be(3);
        tasks.Select(t => t.Id).Should().OnlyHaveUniqueItems();

        // 视角隔离：每个视角任务只携带自己的 prompt，互不依赖（MAF fan-out 并行）、不注入上游
        foreach (var (key, _) in ReviewCommand.ParallelPerspectives)
        {
            var task = tasks.Single(t => t.Id == $"review-{key}");
            task.Prompt.Should().Be($"{key}-PROMPT");
            task.Dependencies.Should().BeEmpty();
            task.InjectUpstreamResults.Should().BeFalse();
        }
    }

    [Fact]
    public void BuildPerspectiveTasks_AggregateWaitsForAllPerspectives()
    {
        var tasks = BuildDefaultTasks();

        var aggregate = tasks.Single(t => t.Id == ReviewCommand.AggregateTaskId);
        aggregate.Dependencies.Should().BeEquivalentTo(
            ReviewCommand.ParallelPerspectives.Select(p => $"review-{p.Key}"));
        aggregate.InjectUpstreamResults.Should().BeTrue();
        aggregate.Prompt.Should().Be("AGGREGATE-PROMPT");
    }

    [Fact]
    public void ParallelReview_AllTasksReadOnly_NoMutatingTools()
    {
        var tasks = BuildDefaultTasks();

        tasks.Should().OnlyContain(t => t.ExecutionAccess == AgentTaskExecutionAccess.ReadOnly);
        var readOnlyTools = PipelineProfileBehavior.ReadOnlyAgentTools;
        readOnlyTools.Should().NotContain(t => t == "Bash" || t == "Edit" || t == "Write");
    }

    [Fact]
    public void BuildPerspectiveTasks_MissingPrompts_YieldsEmpty()
    {
        ReviewCommand.BuildPerspectiveTasks(null, "AGGREGATE").Should().BeEmpty();
        ReviewCommand.BuildPerspectiveTasks(
            ReviewCommand.ParallelPerspectives.ToDictionary(p => p.PromptName, p => "P"), null).Should().BeEmpty();
    }

    [Fact]
    public void PerspectiveGraph_PassesCompilerValidation()
        => AgentTaskWorkflowCompiler.Validate(BuildDefaultTasks());

    [Fact]
    public async Task ExecuteAsync_Default_RunsThreePerspectivesAndAggregates()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "+ Added line", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "abc123 commit", "hash1\nhash2");

        var pm = new PromptManager();
        foreach (var (_, name) in ReviewCommand.ParallelPerspectives)
            pm.RegisterTemplate(new PromptTemplate(name, $"PROMPT:{name} Scope: {{scopeDescription}}"));
        pm.RegisterTemplate(new PromptTemplate(ReviewCommand.AggregatePromptName, "AGGREGATE {{outputFormat}}"));

        var perspectivePrompts = new List<string>();
        string? aggregatePrompt = null;
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                if (request.Description == "aggregate and dedupe review findings")
                {
                    aggregatePrompt = request.Prompt;
                    return Task.FromResult(new AgentRunResult(
                        request.Agent, SessionId.NewId(), "AGGREGATED-REPORT", 1, false));
                }
                lock (perspectivePrompts)
                    perspectivePrompts.Add(request.Prompt);
                return Task.FromResult(new AgentRunResult(
                    request.Agent, SessionId.NewId(), $"[{request.Description}] finding", 1, false));
            });

        var sut = new ReviewCommand(
            gitHelper, pm, new LspDiagnosticRegistry(),
            new ReviewCacheService(NullLogger<ReviewCacheService>.Instance),
            new AgentTaskWorkflowCompiler(runner, NullLogger<AgentTaskWorkflowCompiler>.Instance),
            new AgentTaskWorkflowHost(NullLogger<AgentTaskWorkflowHost>.Instance),
            cacheSafeParams: null);

        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Be("AGGREGATED-REPORT");

        // 三个视角各自独立执行（prompt 隔离）
        perspectivePrompts.Should().HaveCount(3);
        perspectivePrompts.Should().OnlyHaveUniqueItems();
        perspectivePrompts.Should().Contain(p => p.Contains("PROMPT:system/review-security"));
        perspectivePrompts.Should().Contain(p => p.Contains("PROMPT:system/review-performance"));
        perspectivePrompts.Should().Contain(p => p.Contains("PROMPT:system/review-style"));

        // 聚合任务接收全部三视角输出（聚合去重的输入前提）
        aggregatePrompt.Should().NotBeNull();
        aggregatePrompt.Should().Contain("[security perspective review] finding");
        aggregatePrompt.Should().Contain("[performance perspective review] finding");
        aggregatePrompt.Should().Contain("[style perspective review] finding");
    }

    [Fact]
    public async Task ExecuteAsync_MissingPerspectivePrompt_ReturnsErrorWithoutRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        var runner = Substitute.For<IAgentRunner>();
        var sut = new ReviewCommand(
            gitHelper, new PromptManager(), new LspDiagnosticRegistry(),
            new ReviewCacheService(NullLogger<ReviewCacheService>.Instance),
            new AgentTaskWorkflowCompiler(runner, NullLogger<AgentTaskWorkflowCompiler>.Instance),
            new AgentTaskWorkflowHost(NullLogger<AgentTaskWorkflowHost>.Instance),
            cacheSafeParams: null);

        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("system/review-aggregate");
        await runner.DidNotReceive().RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());
    }
}
