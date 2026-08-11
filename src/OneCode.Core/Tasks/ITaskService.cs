namespace OneCode.Core.Tasks;

/// <summary>
/// Task list for agent/background work tracking.
/// Production implementations persist task state so conversation and BuildRun scopes survive restart.
/// </summary>
public interface ITaskService
{
    TaskItem CreateTask(
        string subject,
        string description,
        string? activeForm = null,
        TaskStatus status = TaskStatus.Pending,
        string? owner = null,
        IReadOnlyList<string>? blocks = null,
        IReadOnlyList<string>? blockedBy = null,
        TaskMetadata? metadata = null,
        string? conversationId = null,
        string? buildRunId = null);

    TaskItem? GetTask(string id);
    IReadOnlyList<TaskItem> ListTasks(
        TaskStatus? status = null,
        string? conversationId = null,
        string? buildRunId = null,
        bool exactScope = false);
    bool UpdateTask(string id, string? subject = null, string? description = null, TaskStatus? status = null, string? activeForm = null);
    TaskProjectionResult ProjectTaskStatus(
        string id,
        TaskStatus status,
        string? output = null,
        string? outputKey = null,
        bool requireCompletedDependencies = false);
    bool DeleteTask(string id);
    string GetTaskOutput(string id, int? maxLines = null);
    void AppendTaskOutput(string id, string output);
    string FormatTaskList(
        string? conversationId = null,
        string? buildRunId = null,
        bool exactScope = false);

    /// <summary>
    /// Per-task cancellation token — cancelled when the task transitions to
    /// <see cref="TaskStatus.Cancelled"/> (e.g. via Task tool 'stop' action).
    /// Background executors (BackgroundRun/AgentTool) must link their work to this
    /// token instead of the caller's request token. Returns <see cref="CancellationToken.None"/>
    /// when the task does not exist or has already reached a terminal state.
    /// </summary>
    CancellationToken GetTaskToken(string id);
}

/// <summary>
/// Task status enum — mirrors TaskStatusSchema from TypeScript.
/// </summary>
public enum TaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

public sealed record TaskProjectionResult(
    bool Succeeded,
    TaskItem? Task,
    string? Error = null);

/// <summary>
/// A task item in the task list. Immutable record — all mutations go through
/// <see cref="TaskService"/> which uses per-task locking + copy-on-write.
/// </summary>
public sealed record TaskItem
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ActiveForm { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Pending;
    public string? Owner { get; init; }
    public string? ConversationId { get; init; }
    public string? BuildRunId { get; init; }
    public IReadOnlyList<string> Blocks { get; init; } = ImmutableList<string>.Empty;
    public IReadOnlyList<string> BlockedBy { get; init; } = ImmutableList<string>.Empty;
    public TaskMetadata? Metadata { get; init; }
    public string? OutputLog { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
