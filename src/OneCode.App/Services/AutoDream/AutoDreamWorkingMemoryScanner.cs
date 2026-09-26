using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// Collects the session working-memory artefacts that AutoDream should consolidate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why artefacts come first.</b> Working memory holds what the agent itself wrote down while working —
/// findings, decisions, open questions. Session events only record that a conversation happened, so a
/// consolidator reading events alone has to re-derive conclusions the agent already reached, and cannot
/// tell which of them the agent considered worth keeping.
/// </para>
/// <para>
/// <b>Snapshot, then read.</b> Each artefact's content and timestamp are read once into an immutable
/// snapshot. A file edited while consolidation runs must not appear half-old and half-new in the
/// candidate set, and the caller must be able to attribute every candidate to the exact revision it saw.
/// </para>
/// <para>
/// <b>Read-only.</b> Consolidation never mutates or deletes working memory: whether a working file is
/// obsolete is the agent's judgement, not the consolidator's. Cleanup is driven by the conversation
/// lifecycle instead.
/// </para>
/// </remarks>
public sealed class AutoDreamWorkingMemoryScanner(ILogger<AutoDreamWorkingMemoryScanner>? logger = null)
{
    private readonly ILogger<AutoDreamWorkingMemoryScanner>? _logger = logger;

    /// <summary>
    /// Reads every working-memory file for <paramref name="projectRoot"/> that changed since
    /// <paramref name="since"/>.
    /// </summary>
    /// <param name="projectRoot">Project whose working memory is read. Null yields no artefacts.</param>
    /// <param name="since">Only files modified after this instant are returned.</param>
    /// <param name="maxArtefacts">Upper bound on returned artefacts, newest first.</param>
    /// <param name="ct">Cancellation token.</param>
    public IReadOnlyList<WorkingMemoryArtefact> Collect(
        string? projectRoot,
        DateTimeOffset since,
        int maxArtefacts,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || maxArtefacts <= 0)
            return [];

        var root = FileMemoryStorePaths.ResolveRoot(projectRoot);
        if (!Directory.Exists(root))
        {
            _logger?.LogDebug("AutoDream: no working memory at {Root}", root);
            return [];
        }

        List<WorkingMemoryArtefact> artefacts = [];
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(path);
                if (!info.Exists || info.LastWriteTimeUtc <= since.UtcDateTime)
                    continue;

                var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                artefacts.Add(new WorkingMemoryArtefact(
                    RelativePath: Path.GetRelativePath(root, path),
                    Content: content,
                    LastModifiedAt: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A broken working-memory tree must not fail consolidation: the session-event path still
            // has evidence, and the next cycle can retry once the filesystem recovers.
            _logger?.LogWarning(ex, "AutoDream: failed to read working memory at {Root}", root);
            return [];
        }

        return artefacts
            .OrderByDescending(artefact => artefact.LastModifiedAt)
            .Take(maxArtefacts)
            .ToList();
    }
}

/// <summary>
/// Immutable snapshot of one working-memory file, as read during consolidation.
/// </summary>
/// <param name="RelativePath">Path relative to the working-memory root; identifies the artefact.</param>
/// <param name="Content">File content at snapshot time.</param>
/// <param name="LastModifiedAt">Last write time at snapshot time.</param>
public sealed record WorkingMemoryArtefact(
    string RelativePath,
    string Content,
    DateTimeOffset LastModifiedAt);
