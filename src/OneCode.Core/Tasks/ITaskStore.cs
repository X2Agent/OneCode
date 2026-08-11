namespace OneCode.Core.Tasks;

/// <summary>
/// Durable snapshot store for task state. Implementations must replace the complete
/// snapshot atomically so task mutations can be recovered after process restart.
/// </summary>
public interface ITaskStore
{
    IReadOnlyList<TaskItem> Load();
    void Save(IReadOnlyCollection<TaskItem> tasks);
}
