namespace OneCode.App.Services;

/// <summary>
/// DI-managed facade over <see cref="ReviewCache"/> with deferred commit.
/// <see cref="ScheduleCommit"/> stages hashes when <c>/review</c> returns a Prompt;
/// <see cref="CommitPending"/> persists only after the command prompt stream completes
/// successfully; <see cref="DiscardPending"/> drops the stage on cancel/error.
/// </summary>
public sealed class ReviewCacheService(ILogger<ReviewCacheService> logger)
{
    private readonly object _lock = new();
    private PendingReview? _pending;

    /// <summary>Loads the on-disk cache for <paramref name="baseRef"/> (defaults to HEAD).</summary>
    public ReviewCache Load(string? baseRef) => ReviewCache.Load(baseRef, logger);

    /// <summary>
    /// Stages commit hashes to mark as reviewed after a successful command-prompt stream.
    /// Replaces any previously staged pending set.
    /// </summary>
    public void ScheduleCommit(string? baseRef, IReadOnlyList<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        lock (_lock)
            _pending = new PendingReview(baseRef, [.. hashes]);
    }

    /// <summary>Persists staged hashes if any; no-op when nothing is pending.</summary>
    public void CommitPending()
    {
        PendingReview? pending;
        lock (_lock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is null || pending.Hashes.Count == 0)
            return;

        var cache = Load(pending.BaseRef);
        cache.MarkReviewed(pending.Hashes);
        cache.Save(pending.BaseRef, logger);
        logger.LogDebug(
            "Review cache committed {Count} hash(es) for baseRef={BaseRef}",
            pending.Hashes.Count,
            pending.BaseRef ?? "HEAD");
    }

    /// <summary>Drops staged hashes without writing (cancel / stream failure).</summary>
    public void DiscardPending()
    {
        lock (_lock)
            _pending = null;
    }

    private sealed record PendingReview(string? BaseRef, List<string> Hashes);
}
