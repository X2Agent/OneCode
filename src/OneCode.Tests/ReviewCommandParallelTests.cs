using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Infrastructure.Lsp;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Core.Commands;
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

        var report = result.Should().BeOfType<CommandResult.TextResult>().Which.Value;
        report.Should().StartWith("AGGREGATED-REPORT");

        // 报告尾部附各任务执行摘要（状态 / 耗时 / 轮数 / token）
        report.Should().Contain("- review-security: ok");
        report.Should().Contain("- review-performance: ok");
        report.Should().Contain("- review-style: ok");
        report.Should().Contain("- review-aggregate: ok");

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

    [Fact]
    public async Task ExecuteAsync_AggregateFails_DegradesToRawSectionsWithoutLosingReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "+ Added line", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        var pm = new PromptManager();
        foreach (var (_, name) in ReviewCommand.ParallelPerspectives)
            pm.RegisterTemplate(new PromptTemplate(name, $"PROMPT:{name}"));
        pm.RegisterTemplate(new PromptTemplate(ReviewCommand.AggregatePromptName, "AGGREGATE"));

        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                if (request.Description == "aggregate and dedupe review findings")
                    throw new InvalidOperationException("model overloaded");
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

        // 聚合失败不丢报告：降级输出各视角原始分段 + 失败原因，而不是报错或静默
        var report = result.Should().BeOfType<CommandResult.TextResult>().Which.Value;
        report.Should().Contain("[security perspective review] finding");
        report.Should().Contain("[style perspective review] finding");
        report.Should().Contain("(Aggregation unavailable — aggregation failed: model overloaded");
    }

    [Fact]
    public void FormatAggregateOutput_AggregateOutcomeMissing_DegradesWithTimeoutHint()
    {
        // 整体超时被兜底为 Failed 时聚合 outcome 可能缺失——不得抛
        // "Sequence contains no matching element"，必须降级到原始分段。
        var perspectives = ReviewCommand.ParallelPerspectives.Select(p => new AgentTaskOutcome(
            $"review-{p.Key}", $"{p.Key} perspective review", AgentTaskOutcomeStatus.Succeeded,
            $"[{p.Key}] raw findings", null, 2, false, 1_000)).ToList();
        var result = new AgentWorkflowResult(perspectives, "RAW SECTIONS", 4_000, 6);

        var (output, reviewCompleted) = ReviewCommand.FormatAggregateOutput(result);

        reviewCompleted.Should().BeFalse();
        output.Should().Contain("RAW SECTIONS");
        output.Should().Contain("(Aggregation unavailable — aggregation did not produce a terminal outcome");
    }

    [Fact]
    public void FormatAggregateOutput_Success_CompletesReview()
    {
        var perspectives = ReviewCommand.ParallelPerspectives.Select(p => new AgentTaskOutcome(
            $"review-{p.Key}", $"{p.Key} perspective review", AgentTaskOutcomeStatus.Succeeded,
            "raw", null, 1, false, 500)).ToList();
        perspectives.Add(new AgentTaskOutcome(
            ReviewCommand.AggregateTaskId, "aggregate", AgentTaskOutcomeStatus.Succeeded,
            "FINAL REPORT", null, 1, false, 500, InputTokens: 2_500, OutputTokens: 300));
        var result = new AgentWorkflowResult(perspectives, "FINAL REPORT", 2_000, 4);

        var (output, reviewCompleted) = ReviewCommand.FormatAggregateOutput(result);

        reviewCompleted.Should().BeTrue();
        output.Should().StartWith("FINAL REPORT");
        output.Should().Contain("- review-aggregate: ok");
        output.Should().Contain("2.5k in / 300 out");
    }

    [Fact]
    public void CapDiffForPrompt_UnderLimit_PassesThrough()
    {
        var diff = "+ Added line";
        ReviewCommand.CapDiffForPrompt(diff).Should().BeSameAs(diff);
    }

    [Fact]
    public void CapDiffForPrompt_OverLimit_KeepsWholeFilesAndListsOmitted()
    {
        var chunk = string.Join('\n', Enumerable.Repeat("+ line", 700)) + "\n";
        var diff = string.Join("", Enumerable.Range(1, 40).Select(i =>
            $"diff --git a/file{i}.cs b/file{i}.cs\nindex 1..2\n--- a/file{i}.cs\n+++ b/file{i}.cs\n{chunk}"));
        diff.Length.Should().BeGreaterThan(ReviewCommand.MaxDiffPromptChars);

        var capped = ReviewCommand.CapDiffForPrompt(diff);

        capped.Length.Should().BeLessThan(diff.Length);
        capped.Should().Contain("+++ b/file1.cs");   // 预算内整文件保留
        capped.Should().Contain("- file11.cs");      // 省略清单（file11 起装不下）
        capped.Should().Contain("[diff truncated: showing ");
        capped.Should().Contain("[Narrow the scope:");
    }

    [Fact]
    public void CapDiffForPrompt_SingleFileOverLimit_KeepsTruncatedHead()
    {
        var diff = "diff --git a/huge.cs b/huge.cs\n+++ b/huge.cs\n"
            + string.Join('\n', Enumerable.Repeat("+ line", ReviewCommand.MaxDiffPromptChars));

        var capped = ReviewCommand.CapDiffForPrompt(diff);

        capped.Should().StartWith("diff --git a/huge.cs b/huge.cs");
        capped.Length.Should().BeLessThan(diff.Length);
        capped.Should().Contain("[diff truncated: showing 1 of 1 changed files]");
        capped.Should().Contain("[Narrow the scope:");
    }
}
