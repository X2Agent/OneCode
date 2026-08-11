using System.Text.Json;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="BackgroundWaitTool"/> — covers waiting for running tasks,
/// missing tasks, invalid timeouts, and already-completed tasks.
/// </summary>
public sealed class BackgroundWaitToolTests
{
    [Fact]
    public async Task WaitAsync_RunningTaskCompletes_ReturnsCompletedStatusAndOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        var task = new TaskItem { Id = "wait-1", Status = TaskStatus.InProgress };
        var pollCount = 0;
        taskService.GetTask("wait-1").Returns(_ =>
        {
            pollCount++;
            if (pollCount >= 2)
                return task with { Status = TaskStatus.Completed };
            return task;
        });
        taskService.GetTaskOutput("wait-1").Returns("finished successfully");
        var sut = new BackgroundWaitTool(taskService);

        var result = await sut.WaitAsync("wait-1", timeoutSeconds: 5, ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("taskId").GetString().Should().Be("wait-1");
        doc.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        doc.RootElement.GetProperty("output").GetString().Should().Be("finished successfully");
        pollCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task WaitAsync_NonExistentTask_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("ghost").Returns((TaskItem?)null);
        var sut = new BackgroundWaitTool(taskService);

        var result = await sut.WaitAsync("ghost", timeoutSeconds: 10, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Task #ghost not found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task WaitAsync_InvalidTimeout_ReturnsTimeoutWithoutError(int timeoutSeconds)
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("slow").Returns(new TaskItem { Id = "slow", Status = TaskStatus.InProgress });
        var sut = new BackgroundWaitTool(taskService);

        var result = await sut.WaitAsync("slow", timeoutSeconds: timeoutSeconds, ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("taskId").GetString().Should().Be("slow");
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        doc.RootElement.GetProperty("message").GetString().Should()
            .Be($"Task did not complete within {timeoutSeconds}s");
        taskService.DidNotReceive().GetTaskOutput(Arg.Any<string>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task WaitAsync_AlreadyCompletedTask_ReturnsImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("done").Returns(new TaskItem { Id = "done", Status = TaskStatus.Completed });
        taskService.GetTaskOutput("done").Returns("all good");
        var sut = new BackgroundWaitTool(taskService);

        var result = await sut.WaitAsync("done", timeoutSeconds: 60, ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("taskId").GetString().Should().Be("done");
        doc.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        doc.RootElement.GetProperty("output").GetString().Should().Be("all good");
        taskService.Received(1).GetTask("done");
    }
}
