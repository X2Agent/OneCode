using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Tools;
using OneCode.Core.Domain;
using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class ParallelAgentsToolTests
{
    [Fact]
    public async Task RunParallelAsync_EmptyTasksArray_ReturnsError()
    {
        var sut = CreateTool(Substitute.For<IAgentRunner>());

        var result = await sut.RunParallelAsync([]);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task AIFunctionInvokeAsync_MissingTasks_ReturnsToolErrorInsteadOfThrowing()
    {
        var sut = CreateTool(Substitute.For<IAgentRunner>());
        var method = typeof(ParallelAgentsTool).GetMethod(nameof(ParallelAgentsTool.RunParallelAsync))!;
        var function = AIFunctionFactory.Create(method, name: "ParallelAgents", target: sut);

        var result = await function.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        var json = result.Should().BeOfType<JsonElement>().Subject;
        json.GetProperty("isError").GetBoolean().Should().BeTrue();
        json.GetProperty("content").GetString().Should().Contain("tasks array is required");
    }

    [Fact]
    public async Task RunParallelAsync_InvalidGraph_ReturnsError()
    {
        var sut = CreateTool(Substitute.For<IAgentRunner>());

        var result = await sut.RunParallelAsync([
            new AgentWorkflowTaskInput { Id = "t1", Prompt = "p1" },
            new AgentWorkflowTaskInput { Id = "t2", Prompt = "p2", DependsOn = ["t99"] },
        ]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("t99");
    }

    [Fact]
    public async Task RunParallelAsync_ValidSingleTask_ReturnsJsonResult()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("agent", SessionId.NewId(), "completed work", 2, false));
        var sut = CreateTool(runner);

        var result = await sut.RunParallelAsync([
            new AgentWorkflowTaskInput { Id = "t1", Prompt = "do work" },
        ], ct: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.Content);
        document.RootElement.GetProperty("totalTasks").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("allSucceeded").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("tasks")[0].GetProperty("status").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task RunParallelAsync_WithDependencies_InjectsUpstreamOutput()
    {
        var capturedPrompts = new List<string>();
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<AgentRunRequest>();
                capturedPrompts.Add(request.Prompt);
                var output = request.Description == "research" ? "research-result" : "done";
                return Task.FromResult(new AgentRunResult("agent", SessionId.NewId(), output, 1, false));
            });
        var sut = CreateTool(runner);

        var result = await sut.RunParallelAsync([
            new AgentWorkflowTaskInput { Id = "research", Prompt = "do research", Description = "research" },
            new AgentWorkflowTaskInput { Id = "write", Prompt = "write code", Description = "write", DependsOn = ["research"] },
        ], ct: TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        capturedPrompts.Should().HaveCount(2);
        capturedPrompts[1].Should().Contain("research-result");
        capturedPrompts[1].Should().Contain("write code");
    }

    [Fact]
    public async Task RunParallelAsync_AutoAssignsStableIds_WhenMissing()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("agent", SessionId.NewId(), "ok", 1, false));
        var sut = CreateTool(runner);

        var result = await sut.RunParallelAsync([
            new AgentWorkflowTaskInput { Prompt = "first" },
            new AgentWorkflowTaskInput { Prompt = "second" },
        ], ct: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.Content);
        document.RootElement.GetProperty("totalTasks").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("tasks")[0].GetProperty("taskId").GetString().Should().Be("task_1");
        document.RootElement.GetProperty("tasks")[1].GetProperty("taskId").GetString().Should().Be("task_2");
    }

    private static ParallelAgentsTool CreateTool(IAgentRunner runner)
    {
        var compiler = new AgentTaskWorkflowCompiler(runner, NullLogger<AgentTaskWorkflowCompiler>.Instance);
        var host = new AgentTaskWorkflowHost(NullLogger<AgentTaskWorkflowHost>.Instance);
        return new ParallelAgentsTool(compiler, host);
    }
}
