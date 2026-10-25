using OneCode.App.Query;
using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// 回放会话最近一次持久化的 BuildRun 快照，投影为流内事件；
/// 不重新执行工具，也不产生任何副作用。
/// </summary>
public sealed class BuildRunReplayService(
    IBuildRunStore buildRunStore,
    IBuildRunEventStore buildRunEventStore,
    ILogger<BuildRunReplayService> logger)
{
    public async Task<BuildRunStateEvent?> ReplayLatestAsync(
        SessionId conversationId,
        CancellationToken ct = default)
    {
        var current = await buildRunStore.LoadAsync(conversationId, ct).ConfigureAwait(false);
        if (current is null)
            return null;

        var replayed = await buildRunEventStore.ReplayAsync(current.Id, ct).ConfigureAwait(false);
        if (replayed is null)
        {
            logger.LogWarning(
                "BuildRun {BuildRunId} has a checkpoint but no replayable event sequence; using the validated checkpoint projection.",
                current.Id);
            replayed = current;
        }

        if (replayed.ConversationId != conversationId || replayed.Id != current.Id)
        {
            throw new InvalidDataException(
                $"Replayed BuildRun '{replayed.Id}' does not belong to conversation '{conversationId}'.");
        }

        return BuildRunStateEvent.From(replayed);
    }
}
