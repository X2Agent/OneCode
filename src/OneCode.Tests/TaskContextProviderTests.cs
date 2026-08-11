using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Context;
using OneCode.Core.Tasks;
using System.Reflection;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="TaskContextProvider"/>.
/// Uses reflection to invoke the protected ProvideAIContextAsync method.
/// </summary>
public sealed class TaskContextProviderTests
{
    /// <summary>
    /// Invokes the protected ProvideAIContextAsync via reflection.
    /// The method doesn't use the InvokingContext parameter, so a default instance suffices.
    /// </summary>
    private static async Task<AIContext> InvokeProvideAIContext(TaskContextProvider provider)
    {
        var method = typeof(TaskContextProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.InvokeMethod);

        var contextType = typeof(AIContextProvider).GetNestedType(
            "InvokingContext",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var context = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(contextType);

        var result = (ValueTask<AIContext>)method!.Invoke(provider, [context, CancellationToken.None])!;
        return await result.AsTask();
    }

    [Fact]
    public async Task NoTasks_ReturnsEmptyContext()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(Array.Empty<TaskItem>());
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        (context.Messages ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyCompletedTasks_ReturnsEmptyContext()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(new[]
        {
            new TaskItem { Id = "1", Subject = "Done", Status = TaskStatus.Completed },
        });
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        (context.Messages ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task HasPendingTask_InjectsTaskList()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(new[]
        {
            new TaskItem { Id = "1", Subject = "Write tests", Status = TaskStatus.Pending },
        });
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        context.Messages.Should().HaveCount(1);
        var msg = context.Messages.First();
        msg.Role.Should().Be(ChatRole.System);
        msg.Text.Should().Contain("Write tests");
        msg.Text.Should().Contain("#1");
    }

    [Fact]
    public async Task HasInProgressTask_InjectsWithActiveForm()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(new[]
        {
            new TaskItem
            {
                Id = "2",
                Subject = "Refactor",
                ActiveForm = "Refactoring module",
                Status = TaskStatus.InProgress,
            },
        });
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        var msg = context.Messages.First();
        msg.Text.Should().Contain("Refactor");
        msg.Text.Should().Contain("Refactoring module");
    }

    [Fact]
    public async Task UnresolvedBlocker_ShowsInContext()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(new[]
        {
            new TaskItem
            {
                Id = "3",
                Subject = "Deploy",
                Status = TaskStatus.Pending,
                BlockedBy = new List<string> { "1", "2" },
            },
            new TaskItem { Id = "1", Subject = "Build", Status = TaskStatus.Completed },
            new TaskItem { Id = "2", Subject = "Test", Status = TaskStatus.InProgress },
        });
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        var msg = context.Messages.First();
        // Completed blocker (#1) should be filtered; in-progress blocker (#2) shown
        msg.Text.Should().Contain("blocked by: #2");
        msg.Text.Should().NotContain("blocked by: #1");
    }

    [Fact]
    public async Task TaskWithOwner_ShowsOwnerInContext()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.ListTasks(exactScope: true).Returns(new[]
        {
            new TaskItem
            {
                Id = "4",
                Subject = "Review PR",
                Status = TaskStatus.InProgress,
                Owner = "agent-1",
            },
        });
        var provider = new TaskContextProvider(taskService);

        var context = await InvokeProvideAIContext(provider);

        context.Messages.First().Text.Should().Contain("(agent-1)");
    }
}
