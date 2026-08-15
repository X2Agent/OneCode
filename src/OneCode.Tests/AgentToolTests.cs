using System.Text.Json;
using OneCode.Core.Domain;
using OneCode.Core.Tasks;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

public sealed class AgentToolTests
{
    private static ITaskService CreateTaskService()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTaskToken(Arg.Any<string>()).Returns(CancellationToken.None);
        return taskService;
    }

    private static ICacheSafeParamsProvider NullParams() => new FixedCacheSafeParamsProvider(null);

    private sealed class FixedCacheSafeParamsProvider(CacheSafeParams? current) : ICacheSafeParamsProvider
    {
        public CacheSafeParams? Current { get; } = current;
    }

    [Fact]
    public async Task ExecuteAsync_ValidPrompt_RunsAgentAndReturnsResult()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("general-purpose", SessionId.NewId(), "Task completed successfully", 3, false));

        var sut = new AgentTool(runner, NullParams(), CreateTaskService(), logger: null);

        var resultStr = await sut.RunAgentAsync("Write a test");

        using var doc = JsonDocument.Parse(resultStr.Content);
        var json = doc.RootElement;
        json.GetProperty("agent").GetString().Should().Be("general-purpose");
        json.GetProperty("result").GetString().Should().Be("Task completed successfully");
        await runner.Received(1).RunAsync(
            Arg.Is<AgentRunRequest>(r => r.Prompt == "Write a test"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPrompt_ReturnsError()
    {
        var sut = new AgentTool(Substitute.For<IAgentRunner>(), NullParams(), CreateTaskService(), logger: null);

        var resultStr = await sut.RunAgentAsync("");

        resultStr.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoAgentUsesGeneralPurpose()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("general-purpose", SessionId.NewId(), "Done", 1, false));

        var sut = new AgentTool(runner, NullParams(), CreateTaskService(), logger: null);

        await sut.RunAgentAsync("hello");

        // 默认值必须落在发给 Runner 的请求里；断言响应 JSON 只是 mock 回声，观测不到回归
        await runner.Received(1).RunAsync(
            Arg.Is<AgentRunRequest>(r => r.Agent == "general-purpose"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithCacheSafeParams_PassesToRequest()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("test-agent", SessionId.NewId(), "Done", 1, false));

        var cacheSafeParams = new CacheSafeParams
        {
            SystemPrompt = "parent-system-prompt",
            ModelId = "gpt-4",
            ThinkingBudget = 16000,
        };
        var sut = new AgentTool(runner, new FixedCacheSafeParamsProvider(cacheSafeParams), CreateTaskService(), logger: null);

        var resultStr = await sut.RunAgentAsync("delegate task");

        resultStr.IsError.Should().BeFalse();
        resultStr.Content.Should().Contain("Done");
        await runner.Received(1).RunAsync(
            Arg.Is<AgentRunRequest>(r =>
                r.CacheSafeParams != null &&
                r.CacheSafeParams.SystemPrompt == "parent-system-prompt" &&
                r.CacheSafeParams.ModelId == "gpt-4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BackgroundMode_ReturnsImmediately()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("bg-agent", SessionId.NewId(), "bg done", 5, true));

        var taskService = CreateTaskService();
        taskService.CreateTask(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<OneCode.Core.Tasks.TaskStatus>(), Arg.Any<string?>())
            .Returns(new TaskItem { Id = "task-1", Subject = "test", Description = "desc", Status = OneCode.Core.Tasks.TaskStatus.InProgress });
        var sut = new AgentTool(runner, NullParams(), taskService, logger: null);

        var resultStr = await sut.RunAgentAsync("long task", runInBackground: true);

        using var doc = JsonDocument.Parse(resultStr.Content);
        var json = doc.RootElement;
        json.GetProperty("status").GetString().Should().Be("started");
    }

    [Fact]
    public async Task ExecuteAsync_SpecifiesAgentType_PassesToRequest()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("Explore", SessionId.NewId(), "Explored", 2, false));

        var sut = new AgentTool(runner, NullParams(), CreateTaskService(), logger: null);

        var resultStr = await sut.RunAgentAsync("explore codebase", agent: "Explore");

        using var doc = JsonDocument.Parse(resultStr.Content);
        var json = doc.RootElement;
        json.GetProperty("agent").GetString().Should().Be("Explore");
        json.GetProperty("result").GetString().Should().Be("Explored");
        await runner.Received(1).RunAsync(
            Arg.Is<AgentRunRequest>(r => r.Agent == "Explore"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MaxTurnsReached_ReturnsWarning()
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult("agent", SessionId.NewId(), "Partial result", 10, true));

        var sut = new AgentTool(runner, NullParams(), CreateTaskService(), logger: null);

        var resultStr = await sut.RunAgentAsync("complex task");

        resultStr.Content.Should().Contain("maxTurnsReached");
    }
}
