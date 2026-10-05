using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// GOAL 步内并行（subTasks）守卫：ReadOnly 能力边界、WorkingDirectory 透传（隔离 worktree）、
/// dependsOn 执行顺序、token/变更文件聚合、部分失败映射、任务数上限 fail-closed。
/// </summary>
public sealed class GoalSubTaskDagRunnerTests
{
    [Fact]
    public async Task RunAsync_AllTasksSucceed_AggregatesTokensAndChangedFiles()
    {
        var captured = new List<AgentRunRequest>();
        var runner = StubRunner(captured, _ => "task done");
        var sut = CreateRunner(runner);

        var result = await sut.RunAsync(CreateGoal(), CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(GoalStatus.Completed, "evaluation: {0}; gates: {1}",
            result.Evaluation,
            string.Join(" | ", result.Evidence?.Validations.Select(v => $"{v.Gate}={v.Passed}/{v.Skipped}: {v.Summary}") ?? []));
        result.InputTokens.Should().Be(20, "两个任务的 input token 求和");
        result.OutputTokens.Should().Be(8);
        // fork 事务记录的是绝对路径（cwd=GOAL worktree），聚合保持原样。
        result.Evidence!.ChangedFiles.Should().BeEquivalentTo(["/worktree/a.cs", "/worktree/b.cs"]);
    }

    [Fact]
    public async Task RunAsync_ReadOnlyTask_CapabilitiesExcludeMutatingTools()
    {
        var captured = new List<AgentRunRequest>();
        var sut = CreateRunner(StubRunner(captured, _ => "ok"));

        await sut.RunAsync(CreateGoal(), CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        var readOnlyRequest = captured.Single(r => r.Description == "read stuff");
        readOnlyRequest.CacheSafeParams!.ToolCapabilities!.AllowedToolNames
            .Should().Contain("Read").And.NotContain("Edit")
            .And.NotContain("Write").And.NotContain("Bash");
        var writeRequest = captured.Single(r => r.Description == "write files");
        writeRequest.CacheSafeParams!.ToolCapabilities!.AllowedToolNames
            .Should().Contain("Write", "Write 任务继承父级（GOAL 运行）工具面");
    }

    [Fact]
    public async Task RunAsync_WorkingDirectory_PassedThroughToAllTasks()
    {
        var captured = new List<AgentRunRequest>();
        var sut = CreateRunner(StubRunner(captured, _ => "ok"));

        await sut.RunAsync(CreateGoal(), CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        captured.Should().NotBeEmpty();
        captured.Should().OnlyContain(r => r.WorkingDirectory == "/worktree",
            "DAG 任务必须落在 GOAL 隔离 worktree，而非会话根目录");
    }

    [Fact]
    public async Task RunAsync_DependsOn_WriterRunsAfterReader()
    {
        var captured = new List<AgentRunRequest>();
        var sut = CreateRunner(StubRunner(captured, _ => "ok"));

        await sut.RunAsync(CreateGoal(), CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        // dependsOn: task-w 依赖 task-r——底座按拓扑序调度，r 先于 w 执行。
        var order = captured.Select(r => r.Description).ToList();
        order.IndexOf("read stuff").Should().BeLessThan(order.IndexOf("write files"));
    }

    [Fact]
    public async Task RunAsync_TaskFails_StepFailedWithHardGateEvidence()
    {
        var runner = StubRunner(new List<AgentRunRequest>(), request =>
            request.Description == "write files"
                ? throw new InvalidOperationException("boom")
                : "ok");
        var sut = CreateRunner(runner);

        var result = await sut.RunAsync(CreateGoal(), CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(GoalStatus.Failed);
        result.Evaluation.Should().Contain("FAILED");
        result.Evidence!.Validations.Should().NotBeEmpty("硬门禁仍需对部分成功的工作树实况裁决");
    }

    [Fact]
    public async Task RunAsync_TooManySubTasks_FailClosedWithoutExecuting()
    {
        var captured = new List<AgentRunRequest>();
        var runner = StubRunner(captured, _ => "ok");
        var sut = CreateRunner(runner);
        var goal = CreateGoal();
        goal.SubTasks = Enumerable.Range(1, 6)
            .Select(i => new GoalSubTask($"t{i}", $"task {i}", "do", [], true))
            .ToList();

        var result = await sut.RunAsync(goal, CreateOptions(), CreateChannel(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(GoalStatus.Failed);
        result.Evaluation.Should().Contain("outside the allowed range");
        captured.Should().BeEmpty("fail-closed：非法任务数不得执行任何任务");
    }

    private static ChannelWriter<TuiEvent> CreateChannel()
        => Channel.CreateUnbounded<TuiEvent>().Writer;

    private static GoalSubTaskDagRunner CreateRunner(IAgentRunner runner)
    {
        // .cs 变更会触发 build 门禁——stub 一个验证通过的 provider（GateDependencies 可选参数为 null）。
        var verification = Substitute.For<IVerificationProvider>();
        verification.VerifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationResult { Success = true, Errors = [] });
        verification.VerifyBuildAndTestsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationResult { Success = true, Errors = [] });

        return new GoalSubTaskDagRunner(
            runner,
            new AgentTaskWorkflowCompiler(runner, NullLogger<AgentTaskWorkflowCompiler>.Instance),
            new AgentTaskWorkflowHost(NullLogger<AgentTaskWorkflowHost>.Instance),
            new GoalSubGoalGateDependencies(verification, null),
            NullLoggerFactory.Instance,
            NullLogger<GoalSubTaskDagRunner>.Instance);
    }

    private static IAgentRunner StubRunner(
        List<AgentRunRequest> captured,
        Func<AgentRunRequest, string> respond)
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                lock (captured)
                    captured.Add(request);
                string output;
                try
                {
                    output = respond(request);
                }
                catch (Exception ex)
                {
                    return System.Threading.Tasks.Task.FromResult(new AgentRunResult(
                        request.Agent,
                        SessionId.NewId(),
                        null,
                        1,
                        false,
                        OneCode.Core.Errors.AgentProblemDetails.ToolExecutionFailed(
                            detail: ex.Message, toolName: request.Agent)));
                }
                return System.Threading.Tasks.Task.FromResult(new AgentRunResult(
                    request.Agent,
                    SessionId.NewId(),
                    output,
                    1,
                    false,
                    ChangedFiles: request.Description == "write files" ? ["/worktree/a.cs"] : ["/worktree/b.cs"],
                    InputTokens: 10,
                    OutputTokens: 4));
            });
        return runner;
    }

    private static GoalItem CreateGoal() => new()
    {
        Id = 5,
        Description = "Parallel docs step",
        SuccessCriteria = "All parallel sub-tasks done",
        SubTasks =
        [
            new GoalSubTask("task-r", "read stuff", "Read the files.", [], ReadOnly: true),
            new GoalSubTask("task-w", "write files", "Write the files.", ["task-r"], ReadOnly: false),
        ],
    };

    private static GoalRunOptions CreateOptions() => new()
    {
        Goal = "goal",
        WorkingDirectory = "/worktree",
        ModelId = "model",
        Tools = new List<AITool>
        {
            AIFunctionFactory.Create(static () => "", name: "Read"),
            AIFunctionFactory.Create(static () => "", name: "Write"),
            AIFunctionFactory.Create(static () => "", name: "Bash"),
        },
    };
}
