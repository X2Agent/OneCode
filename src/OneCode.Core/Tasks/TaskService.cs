namespace OneCode.Core.Tasks;

/// <summary>
/// Task list used by AgentTool / WorkerAgentService / Background* tools.
/// When constructed with an <see cref="ITaskStore"/>, snapshots are restored at startup
/// and atomically persisted after every mutation.
/// </summary>
/// <remarks>
/// <see cref="TaskItem"/> is an immutable record. All mutations use per-task locking
/// + copy-on-write (<c>with</c> expressions) to guarantee thread-safe reads/writes.
/// </remarks>
public sealed class TaskService : ITaskService
{
    private readonly ITaskStore? _store;
    private readonly object _persistenceGate = new();
    private readonly ConcurrentDictionary<string, TaskItem> _tasks = new();

    // Per-task lock objects — serialize mutations to a single task instance.
    private readonly ConcurrentDictionary<string, object> _taskLocks = new();

    // Per-task cancellation source. Cancelled on transition to Cancelled (Task 'stop'),
    // removed on any terminal state to bound memory. Never disposed — in-flight
    // CancellationToken registrations may still fire after removal, and disposing
    // would race them with ObjectDisposedException. CTS without WaitHandle access
    // allocates no kernel handle, so GC collection is sufficient.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCts = new();
    private int _nextId;

    public TaskService(ITaskStore? store = null)
    {
        _store = store;
        var restored = store?.Load() ?? [];
        foreach (var task in restored)
        {
            _tasks[task.Id] = task;
            if (task.Status is TaskStatus.Pending or TaskStatus.InProgress)
                _taskCts[task.Id] = new CancellationTokenSource();
            if (int.TryParse(task.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                _nextId = Math.Max(_nextId, id);
        }
    }

    private object GetLock(string id) => _taskLocks.GetOrAdd(id, _ => new object());

    private void PersistSnapshot()
    {
        if (_store is null)
            return;
        lock (_persistenceGate)
            _store.Save(_tasks.Values.ToArray());
    }

    public TaskItem CreateTask(
        string subject,
        string description,
        string? activeForm = null,
        TaskStatus status = TaskStatus.Pending,
        string? owner = null,
        IReadOnlyList<string>? blocks = null,
        IReadOnlyList<string>? blockedBy = null,
        TaskMetadata? metadata = null,
        string? conversationId = null,
        string? buildRunId = null)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var task = new TaskItem
        {
            Id = id,
            Subject = subject,
            Description = description,
            ActiveForm = activeForm,
            Status = status,
            Owner = owner,
            ConversationId = conversationId,
            BuildRunId = buildRunId,
            Blocks = blocks?.ToImmutableList() ?? ImmutableList<string>.Empty,
            BlockedBy = blockedBy?.ToImmutableList() ?? ImmutableList<string>.Empty,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _tasks[id] = task;
        _taskCts[id] = new CancellationTokenSource();
        PersistSnapshot();
        return task;
    }

    public CancellationToken GetTaskToken(string id)
        => _taskCts.TryGetValue(id, out var cts) ? cts.Token : CancellationToken.None;

    public TaskItem? GetTask(string id)
    {
        return _tasks.TryGetValue(id, out var task) ? task : null;
    }

    public IReadOnlyList<TaskItem> ListTasks(
        TaskStatus? status = null,
        string? conversationId = null,
        string? buildRunId = null,
        bool exactScope = false)
    {
        var tasks = _tasks.Values.ToList();
        if (exactScope)
        {
            tasks = tasks.Where(t =>
                string.Equals(t.ConversationId, conversationId, StringComparison.Ordinal)
                && string.Equals(t.BuildRunId, buildRunId, StringComparison.Ordinal)).ToList();
        }
        else
        {
            if (conversationId is not null)
                tasks = tasks.Where(t => t.ConversationId == conversationId).ToList();
            if (buildRunId is not null)
                tasks = tasks.Where(t => t.BuildRunId == buildRunId).ToList();
        }
        if (status.HasValue)
            tasks = tasks.Where(t => t.Status == status.Value).ToList();
        return tasks;
    }

    public bool UpdateTask(string id, string? subject = null, string? description = null, TaskStatus? status = null, string? activeForm = null)
    {
        lock (GetLock(id))
        {
            if (!_tasks.TryGetValue(id, out var current))
                return false;

            var updated = current with
            {
                Subject = subject ?? current.Subject,
                Description = description ?? current.Description,
                Status = status ?? current.Status,
                ActiveForm = activeForm ?? current.ActiveForm,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _tasks[id] = updated;
        }

        if (status is TaskStatus.Cancelled)
            CancelTaskToken(id);
        else if (status is TaskStatus.Completed or TaskStatus.Failed)
            _taskCts.TryRemove(id, out _);

        PersistSnapshot();
        return true;
    }

    public TaskProjectionResult ProjectTaskStatus(
        string id,
        TaskStatus status,
        string? output = null,
        string? outputKey = null,
        bool requireCompletedDependencies = false)
    {
        TaskItem updated;
        lock (GetLock(id))
        {
            if (!_tasks.TryGetValue(id, out var current))
                return new TaskProjectionResult(false, null, $"Task '{id}' was not found.");

            if (requireCompletedDependencies && status is TaskStatus.InProgress or TaskStatus.Completed)
            {
                var unresolved = current.BlockedBy
                    .Where(dependencyId => !_tasks.TryGetValue(dependencyId, out var dependency)
                        || dependency.Status != TaskStatus.Completed)
                    .ToArray();
                if (unresolved.Length > 0)
                {
                    return new TaskProjectionResult(
                        false,
                        current,
                        $"Task '{id}' is blocked by incomplete dependencies: {string.Join(", ", unresolved)}.");
                }
            }

            var outputLog = current.OutputLog ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(output))
            {
                var marker = string.IsNullOrWhiteSpace(outputKey)
                    ? null
                    : $"[projection:{outputKey}]";
                if (marker is null || !outputLog.Contains(marker, StringComparison.Ordinal))
                {
                    outputLog += marker is null
                        ? output + "\n"
                        : marker + " " + output + "\n";
                }
            }

            updated = current with
            {
                Status = status,
                OutputLog = outputLog,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _tasks[id] = updated;

            if (_store is not null)
            {
                lock (_persistenceGate)
                    _store.Save(_tasks.Values.ToArray());
            }
        }

        if (status is TaskStatus.Cancelled)
            CancelTaskToken(id);
        else if (status is TaskStatus.Completed or TaskStatus.Failed)
            _taskCts.TryRemove(id, out _);

        return new TaskProjectionResult(true, updated);
    }

    public bool DeleteTask(string id)
    {
        CancelTaskToken(id);
        var removed = _tasks.TryRemove(id, out _);
        _taskLocks.TryRemove(id, out _);
        if (removed)
            PersistSnapshot();
        return removed;
    }

    private void CancelTaskToken(string id)
    {
        if (_taskCts.TryRemove(id, out var cts))
            cts.Cancel();
    }

    public string GetTaskOutput(string id, int? maxLines = null)
    {
        if (!_tasks.TryGetValue(id, out var task))
            return $"Task #{id} not found";

        // Flush any pending buffer content into the snapshot.
        string output;
        lock (GetLock(id))
        {
            if (_tasks.TryGetValue(id, out var current))
            {
                output = current.OutputLog ?? "";
            }
            else
            {
                return $"Task #{id} not found";
            }
        }

        if (maxLines.HasValue)
        {
            var lines = output.Split('\n');
            if (lines.Length > maxLines.Value)
                output = string.Join("\n", lines.Skip(lines.Length - maxLines.Value));
        }

        return output;
    }

    public void AppendTaskOutput(string id, string output)
    {
        lock (GetLock(id))
        {
            if (!_tasks.TryGetValue(id, out var current))
                return;

            // Persist output immediately. Background task output is recovery evidence and
            // must not remain only in an in-memory buffer when a durable store is configured.
            _tasks[id] = current with
            {
                OutputLog = (current.OutputLog ?? "") + output + "\n",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        PersistSnapshot();
    }

    public string FormatTaskList(
        string? conversationId = null,
        string? buildRunId = null,
        bool exactScope = false)
    {
        var tasks = ListTasks(
            conversationId: conversationId,
            buildRunId: buildRunId,
            exactScope: exactScope);
        tasks = tasks.OrderBy(t => t.CreatedAt).ToList();
        if (tasks.Count == 0)
            return "No tasks found";

        var resolvedIds = new HashSet<string>(tasks.Where(t => t.Status == TaskStatus.Completed).Select(t => t.Id));

        var lines = tasks.Select(task =>
        {
            var owner = !string.IsNullOrEmpty(task.Owner) ? $" ({task.Owner})" : "";
            var blockedBy = task.BlockedBy.Where(id => !resolvedIds.Contains(id)).ToList();
            var blocked = blockedBy.Count > 0 ? $" [blocked by {string.Join(", ", blockedBy.Select(id => $"#{id}"))}]" : "";
            return $"#{task.Id} [{task.Status}] {task.Subject}{owner}{blocked}";
        }).ToList();

        return string.Join("\n", lines);
    }
}
