using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Observes orchestration events and merges FileChanged into a FileChange list.
/// Same-file edits (possibly from different members) merge diffs and Contributors.
/// </summary>
internal static class TeamFileChangeObserver
{
    /// <summary>
    /// Wraps <paramref name="eventSink"/>; accumulates FileChanged into the returned list.
    /// </summary>
    public static (List<FileChange> FileChanges, Action<OrchestrationEvent>? ObservedSink)
        CreateObservedSink(Action<OrchestrationEvent>? eventSink)
    {
        List<FileChange> fileChanges = [];
        var fileIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Action<OrchestrationEvent>? observedSink = evt =>
        {
            if (evt is OrchestrationEvent.FileChanged changed)
            {
                if (fileIndex.TryGetValue(changed.FileName, out var idx))
                {
                    var existing = fileChanges[idx];
                    var contributors = existing.Contributors is { Count: > 0 } c && !c.Contains(changed.AgentName)
                        ? [.. c, changed.AgentName]
                        : existing.Contributors ?? (changed.AgentName is null ? null : [changed.AgentName]);
                    fileChanges[idx] = new FileChange(
                        changed.FileName,
                        [.. existing.AddedLines, .. changed.AddedLines],
                        [.. existing.RemovedLines, .. changed.RemovedLines],
                        contributors);
                }
                else
                {
                    fileIndex[changed.FileName] = fileChanges.Count;
                    fileChanges.Add(new FileChange(
                        changed.FileName,
                        changed.AddedLines,
                        changed.RemovedLines,
                        changed.AgentName is null ? null : [changed.AgentName]));
                }
            }
            eventSink?.Invoke(evt);
        };
        return (fileChanges, observedSink);
    }
}
