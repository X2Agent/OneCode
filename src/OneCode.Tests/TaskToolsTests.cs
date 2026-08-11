using System.Text.Json;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Tools;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for the unified TaskTool — covers create, get, list, stop, update, and output via action routing.
/// </summary>
public sealed class TaskToolsTests
{
    private static TaskTool CreateSut(ITaskService taskService) => new(taskService);

    // Create

    [Fact]
    public async Task Create_ValidInput_ReturnsCreatedTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.CreateTask("Run tests", "Execute unit tests", "Running tests")
            .Returns(new TaskItem
            {
                Id = "42",
                Subject = "Run tests",
                Description = "Execute unit tests",
                ActiveForm = "Running tests",
            });
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("create", subject: "Run tests", description: "Execute unit tests", activeForm: "Running tests", ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        var task = doc.RootElement.GetProperty("task");
        task.GetProperty("id").GetString().Should().Be("42");
        task.GetProperty("subject").GetString().Should().Be("Run tests");
        taskService.Received(1).CreateTask("Run tests", "Execute unit tests", "Running tests");
    }

    [Fact]
    public async Task Create_EmptySubject_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("create", subject: "", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("subject is required");
        taskService.DidNotReceive().CreateTask(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // Get

    [Fact]
    public async Task Get_ExistingTask_ReturnsTaskDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("7").Returns(new TaskItem
        {
            Id = "7",
            Subject = "Build project",
            Description = "Compile solution",
            ActiveForm = "Building",
            Status = TaskStatus.InProgress,
            Owner = "agent-1",
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
        });
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("get", taskId: "7", ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("Id").GetString().Should().Be("7");
        doc.RootElement.GetProperty("Subject").GetString().Should().Be("Build project");
        doc.RootElement.GetProperty("Status").GetString().Should().Be("InProgress");
        doc.RootElement.GetProperty("Owner").GetString().Should().Be("agent-1");
    }

    [Fact]
    public async Task Get_NonExistentTask_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("missing").Returns((TaskItem?)null);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("get", taskId: "missing", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Task #missing not found");
    }

    // List

    [Fact]
    public async Task List_ReturnsFormattedTaskList()
    {
        var ct = TestContext.Current.CancellationToken;
        const string formatted = "1. [in_progress] Run tests\n2. [pending] Deploy";
        var taskService = Substitute.For<ITaskService>();
        taskService.FormatTaskList(exactScope: true).Returns(formatted);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("list", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be(formatted);
        taskService.Received(1).FormatTaskList(exactScope: true);
    }

    // Stop

    [Fact]
    public async Task Stop_RunningTask_CancelsAndReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("3").Returns(new TaskItem { Id = "3", Status = TaskStatus.InProgress });
        taskService.UpdateTask("3", subject: null, description: null, status: TaskStatus.Cancelled, activeForm: null)
            .Returns(true);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("stop", taskId: "3", ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("task").GetProperty("status").GetString().Should().Be("cancelled");
        taskService.Received(1).UpdateTask("3", subject: null, description: null, status: TaskStatus.Cancelled, activeForm: null);
    }

    [Fact]
    public async Task Stop_AlreadyCompletedTask_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("9").Returns(new TaskItem { Id = "9", Status = TaskStatus.Completed });
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("stop", taskId: "9", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Task #9 is already Completed");
    }

    // Update

    [Fact]
    public async Task Update_ExistingTask_ReturnsUpdatedTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("5").Returns(new TaskItem
        {
            Id = "5",
            Subject = "Old title",
            Description = "Old description",
            Status = TaskStatus.InProgress,
        });
        taskService.UpdateTask("5", "New title", "New description", TaskStatus.Completed, "Finishing up")
            .Returns(true);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("update", taskId: "5", subject: "New title", description: "New description", status: "completed", activeForm: "Finishing up", ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("task").GetProperty("status").GetString().Should().Be("Completed");
        taskService.Received(1).UpdateTask("5", "New title", "New description", TaskStatus.Completed, "Finishing up");
    }

    [Fact]
    public async Task Update_NonExistentTask_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        taskService.UpdateTask("missing", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskStatus?>(), Arg.Any<string?>())
            .Returns(false);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("update", taskId: "missing", subject: "Title", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Task #missing not found");
    }

    // Output

    [Fact]
    public async Task Output_ExistingTaskWithOutput_ReturnsOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        const string output = "Build succeeded.\n2 tests passed.";
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTask("10").Returns(new TaskItem { Id = "10", Status = TaskStatus.Completed });
        taskService.GetTaskOutput("10", null).Returns(output);
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("output", taskId: "10", ct: ct);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("output").GetString().Should().Be(output);
    }

    [Fact]
    public async Task Get_TaskFromDifferentBuildRun_ReturnsNotFound()
    {
        var taskService = new TaskService();
        var task = taskService.CreateTask(
            "Scoped",
            "Scoped task",
            conversationId: "conv-1",
            buildRunId: "br-1");
        ToolActivationContext.CurrentConversationId = "conv-1";
        OneCodeAgentRunContext.CurrentBuildRunId = "br-2";
        try
        {
            var result = await CreateSut(taskService).ExecuteAsync(
                "get",
                taskId: task.Id,
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeTrue();
            result.Content.Should().Be($"Task #{task.Id} not found");
        }
        finally
        {
            OneCodeAgentRunContext.CurrentBuildRunId = null;
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Fact]
    public async Task Get_NullBuildRunScope_CannotReadBuildRunTask()
    {
        var taskService = new TaskService();
        var task = taskService.CreateTask(
            "Scoped",
            "Build task",
            conversationId: "conv-1",
            buildRunId: "br-1");
        ToolActivationContext.CurrentConversationId = "conv-1";
        OneCodeAgentRunContext.CurrentBuildRunId = null;
        try
        {
            var result = await CreateSut(taskService).ExecuteAsync(
                "get",
                taskId: task.Id,
                ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeTrue();
            result.Content.Should().Be($"Task #{task.Id} not found");
        }
        finally
        {
            OneCodeAgentRunContext.CurrentBuildRunId = null;
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Fact]
    public async Task List_UsesConversationAndBuildRunScope()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.FormatTaskList("conv-1", "br-1", exactScope: true).Returns("scoped");
        ToolActivationContext.CurrentConversationId = "conv-1";
        OneCodeAgentRunContext.CurrentBuildRunId = "br-1";
        try
        {
            var result = await CreateSut(taskService).ExecuteAsync(
                "list",
                ct: TestContext.Current.CancellationToken);

            result.Content.Should().Be("scoped");
            taskService.Received(1).FormatTaskList("conv-1", "br-1", exactScope: true);
        }
        finally
        {
            OneCodeAgentRunContext.CurrentBuildRunId = null;
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    // Invalid action

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("delete", taskId: "1", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown action 'delete'");
    }

    [Fact]
    public async Task Get_MissingTaskId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskService = Substitute.For<ITaskService>();
        var sut = CreateSut(taskService);

        var result = await sut.ExecuteAsync("get", taskId: null, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("taskId is required");
    }
}
