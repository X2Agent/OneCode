using OneCode.App.Query;
using OneCode.App.Tui;
using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Replays the canonical persisted BuildRun snapshot for a conversation and
/// projects it to the TUI without re-executing tools or other side effects.
/// </summary>
public sealed class BuildRunTuiReplayService(
    IBuildRunStore buildRunStore,
    IBuildRunEventStore buildRunEventStore,
    ILogger<BuildRunTuiReplayService> logger)
{
    public async Task<TuiBuildRunState?> ReplayLatestAsync(
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

        return (TuiBuildRunState?)TuiEventMapper.MapQueryEventToTuiEvent(
            BuildRunStateEvent.From(replayed));
    }
}
