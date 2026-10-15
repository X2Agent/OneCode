using Microsoft.Agents.AI;
using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Resolves the store root for Harness session working memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the Harness default.</b> When no store is supplied, Harness roots working memory at
/// <c>{Directory.GetCurrentDirectory()}/agent-file-memory</c>. That is the <b>process</b> directory,
/// not the session's project: switching projects with <c>/cd</c> or opening a different workspace would
/// silently read and write a different working-memory tree, and two sessions in different projects
/// running from the same process directory would collide.
/// </para>
/// <para>
/// The root is therefore derived from the session's working directory and kept inside the project's own
/// <c>.onecode</c> directory, next to the long-term <c>MEMORY.md</c>. The two stay separate
/// (<c>agent-file-memory</c> vs <c>memory</c>) because they hold different things: raw working notes
/// versus curated knowledge.
/// </para>
/// </remarks>
public static class FileMemoryStorePaths
{
    /// <summary>Subdirectory of <c>.onecode</c> that holds session working memory.</summary>
    public const string WorkingMemoryDirName = "agent-file-memory";

    /// <summary>
    /// Returns the working-memory root for a project.
    /// </summary>
    /// <param name="workingDirectory">The session's project directory.</param>
    /// <returns>
    /// <c>{workingDirectory}/.onecode/agent-file-memory</c>, normalized to an absolute path.
    /// </returns>
    public static string ResolveRoot(string workingDirectory)
    {
        var normalized = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);

        return Path.Combine(normalized, Constants.App.ConfigDirName, WorkingMemoryDirName);
    }

    /// <summary>
    /// Creates the working-memory store for a project.
    /// </summary>
    /// <param name="workingDirectory">The session's project directory.</param>
    /// <remarks>
    /// The store defers all directory creation to the first write (see
    /// <see cref="LazyRootAgentFileStore"/>): Harness assigns a fresh working folder per session
    /// and eagerly creates it on every run, so an eagerly-created root would accumulate one empty
    /// directory per session on disk.
    /// </remarks>
    public static AgentFileStore CreateStore(string workingDirectory)
        => new LazyRootAgentFileStore(ResolveRoot(workingDirectory));

    /// <summary>
    /// Wraps <see cref="FileSystemAgentFileStore"/> so that nothing before the first write touches
    /// the disk. The inner store's constructor eagerly creates the root, so it is only materialized
    /// on write; reads, listings, searches and deletes over a not-yet-existing root short-circuit
    /// to empty results without constructing the inner store at all.
    /// </summary>
    private sealed class LazyRootAgentFileStore(string rootDirectory) : AgentFileStore
    {
        // 双重创建无害（CreateDirectory 幂等，后创建者胜出），不加锁。
        private FileSystemAgentFileStore? _inner;

        private FileSystemAgentFileStore Inner => _inner ??= new FileSystemAgentFileStore(rootDirectory);

        public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
            => Inner.WriteAsync(path, content, cancellationToken);

        public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
            => RootMissing ? Task.FromResult<string?>(null) : Inner.ReadAsync(path, cancellationToken);

        public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
            => RootMissing ? Task.FromResult(false) : Inner.DeleteAsync(path, cancellationToken);

        public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => RootMissing ? Task.FromResult(false) : Inner.FileExistsAsync(path, cancellationToken);

        public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
            => RootMissing
                ? Task.FromResult<IReadOnlyList<FileStoreEntry>>([])
                : Inner.ListChildrenAsync(directory, cancellationToken);

        public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string directory, string regexPattern, string? globPattern = null, bool recursive = false, CancellationToken cancellationToken = default)
            => RootMissing
                ? Task.FromResult<IReadOnlyList<FileSearchResult>>([])
                : Inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);

        // FileMemoryProvider 每个 run 都会对会话工作目录调用此方法；实际目录由首次写入时
        // WriteAsync 的逐级建目录完成，这里不再响应。
        public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private bool RootMissing => !Directory.Exists(rootDirectory);
    }
}
