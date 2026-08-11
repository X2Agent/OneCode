using OneCode.Core.Tasks;
using OneCode.Infrastructure.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

public sealed class TaskServicePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodeTaskStoreTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void JsonStore_Restart_RestoresScopedTasksOutputAndIdSequence()
    {
        var first = new TaskService(new JsonTaskStore(_root));
        var created = first.CreateTask(
            "Implement Build M2",
            "Persist task state",
            status: TaskStatus.InProgress,
            conversationId: "conv-1",
            buildRunId: "br-1");
        first.AppendTaskOutput(created.Id, "checkpoint saved");
        first.UpdateTask(created.Id, status: TaskStatus.Completed);

        var restored = new TaskService(new JsonTaskStore(_root));
        var task = restored.GetTask(created.Id);
        var next = restored.CreateTask("Next", "Continue");

        task.Should().NotBeNull();
        task!.ConversationId.Should().Be("conv-1");
        task.BuildRunId.Should().Be("br-1");
        task.Status.Should().Be(TaskStatus.Completed);
        restored.GetTaskOutput(created.Id).Should().Contain("checkpoint saved");
        int.Parse(next.Id, CultureInfo.InvariantCulture)
            .Should().Be(int.Parse(created.Id, CultureInfo.InvariantCulture) + 1);
    }

    [Fact]
    public void ListTasks_FiltersByConversationAndBuildRun()
    {
        var sut = new TaskService();
        sut.CreateTask("A", "A", conversationId: "conv-1", buildRunId: "br-1");
        sut.CreateTask("B", "B", conversationId: "conv-1", buildRunId: "br-2");
        sut.CreateTask("C", "C", conversationId: "conv-2", buildRunId: "br-1");

        var scoped = sut.ListTasks(conversationId: "conv-1", buildRunId: "br-1");

        scoped.Should().ContainSingle().Which.Subject.Should().Be("A");
    }

    [Fact]
    public void ListTasks_ExactNullScope_DoesNotReturnBuildRunTasks()
    {
        var sut = new TaskService();
        sut.CreateTask("Conversation task", "No BuildRun", conversationId: "conv-1");
        sut.CreateTask("Build task", "Scoped to BuildRun", conversationId: "conv-1", buildRunId: "br-1");
        sut.CreateTask("Global task", "No conversation or BuildRun");

        var conversationTasks = sut.ListTasks(
            conversationId: "conv-1",
            buildRunId: null,
            exactScope: true);
        var globalTasks = sut.ListTasks(exactScope: true);

        conversationTasks.Should().ContainSingle()
            .Which.Subject.Should().Be("Conversation task");
        globalTasks.Should().ContainSingle()
            .Which.Subject.Should().Be("Global task");
    }

    [Fact]
    public void ProjectTaskStatus_EnforcesDependenciesAndPersistsEvidenceAtomically()
    {
        var service = new TaskService(new JsonTaskStore(_root));
        var dependency = service.CreateTask(
            "Implementation",
            "Implement",
            status: TaskStatus.InProgress,
            conversationId: "conv-1",
            buildRunId: "br-1");
        var blocked = service.CreateTask(
            "Verification",
            "Verify",
            blockedBy: [dependency.Id],
            conversationId: "conv-1",
            buildRunId: "br-1");

        var rejected = service.ProjectTaskStatus(
            blocked.Id,
            TaskStatus.InProgress,
            requireCompletedDependencies: true);
        service.ProjectTaskStatus(dependency.Id, TaskStatus.Completed).Succeeded.Should().BeTrue();
        var projected = service.ProjectTaskStatus(
            blocked.Id,
            TaskStatus.Completed,
            "verification evidence",
            "workflow:2:verification",
            requireCompletedDependencies: true);
        var replay = service.ProjectTaskStatus(
            blocked.Id,
            TaskStatus.Completed,
            "verification evidence",
            "workflow:2:verification",
            requireCompletedDependencies: true);
        var restored = new TaskService(new JsonTaskStore(_root));

        rejected.Succeeded.Should().BeFalse();
        rejected.Error.Should().Contain(dependency.Id);
        projected.Succeeded.Should().BeTrue();
        replay.Succeeded.Should().BeTrue();
        restored.GetTask(blocked.Id)!.Status.Should().Be(TaskStatus.Completed);
        restored.GetTaskOutput(blocked.Id).Split("verification evidence").Length.Should().Be(2);
    }

    [Fact]
    public void JsonStore_WhenPrimaryIsCorrupt_RecoversPreviousValidBackup()
    {
        var service = new TaskService(new JsonTaskStore(_root));
        var first = service.CreateTask("First", "First snapshot");
        _ = service.CreateTask("Second", "Creates backup");
        File.WriteAllText(Path.Combine(_root, "tasks.json"), "{ corrupt");

        var restored = new TaskService(new JsonTaskStore(_root));

        restored.GetTask(first.Id).Should().NotBeNull();
        restored.ListTasks().Should().ContainSingle();
    }

    [Fact]
    public void JsonStore_WhenPrimaryAndBackupAreCorrupt_FailsClosed()
    {
        var service = new TaskService(new JsonTaskStore(_root));
        _ = service.CreateTask("First", "First snapshot");
        _ = service.CreateTask("Second", "Creates backup");
        File.WriteAllText(Path.Combine(_root, "tasks.json"), "{ corrupt");
        File.WriteAllText(Path.Combine(_root, "tasks.json.bak"), "{ corrupt");

        var act = () => new TaskService(new JsonTaskStore(_root));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*corrupt or incompatible*");
    }

    [Fact]
    public void ConcurrentMutations_RestartRestoresCompleteSnapshot()
    {
        var service = new TaskService(new JsonTaskStore(_root));

        Parallel.For(
            0,
            24,
            index =>
            {
                var task = service.CreateTask(
                    $"Task {index}",
                    "Concurrent mutation",
                    conversationId: "conv-1",
                    buildRunId: "br-1");
                service.AppendTaskOutput(task.Id, $"output-{index}");
                service.UpdateTask(task.Id, status: TaskStatus.Completed);
            });

        var restored = new TaskService(new JsonTaskStore(_root));
        var tasks = restored.ListTasks(conversationId: "conv-1", buildRunId: "br-1");

        tasks.Should().HaveCount(24);
        tasks.Should().OnlyContain(task => task.Status == TaskStatus.Completed);
        tasks.Should().OnlyContain(task =>
            restored.GetTaskOutput(task.Id).Contains("output-", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
