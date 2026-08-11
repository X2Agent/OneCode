namespace OneCode.Core.Commands;

/// <summary>
/// Git 辅助服务契约。
/// </summary>
public interface IGitHelper
{
    /// <summary>
    /// Attempt to detect a Git installation by running <c>git --version</c>.
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// 执行 <c>git [arguments...]</c> 并返回结构化结果。
    /// 返回 <c>null</c> 表示 git 不可用或执行抛异常。
    /// </summary>
    Task<GitCommandResult?> RunAsync(string[] arguments, CancellationToken ct = default);

    /// <summary>
    /// 在指定工作目录执行 git 命令。
    /// </summary>
    Task<GitCommandResult?> RunAsync(
        string[] arguments,
        string? workingDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// 执行 <c>git [arguments...]</c> 并返回格式化字符串。
    /// </summary>
    Task<string> ReadAsync(string[] arguments, CancellationToken ct = default);

    /// <summary>
    /// 在指定工作目录执行 git 并返回格式化字符串。
    /// </summary>
    Task<string> ReadAsync(
        string[] arguments,
        string? workingDirectory,
        CancellationToken ct = default);

    /// <summary>rev-parse --show-toplevel; null if not a repo.</summary>
    Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>status --porcelain line count; null = git error (distinct from 0).</summary>
    Task<int?> CountPorcelainChangesAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>
    /// <c>diff --numstat HEAD</c> → review file entries.
    /// Rename paths from git's <c>{old =&gt; new}</c> syntax are resolved to the new path
    /// so subsequent <see cref="GetFileDiffAgainstHeadAsync"/> calls succeed.
    /// </summary>
    Task<IReadOnlyList<ReviewFileEntry>> GetPendingDiffStatAsync(
        CancellationToken ct = default,
        string? workingDirectory = null);

    /// <summary>
    /// Unified diff for one path vs HEAD (staged + unstaged).
    /// Accepts raw numstat rename paths; they are normalized before invoking git.
    /// Always resolves the repo root and uses <c>:(top)</c> pathspecs so this works
    /// when the process cwd is a subdirectory (e.g. <c>dotnet run</c> from Cli).
    /// </summary>
    Task<string> GetFileDiffAgainstHeadAsync(
        string filePath,
        CancellationToken ct = default,
        string? workingDirectory = null);
}

/// <summary>Review overlay 展示的变更文件条目。</summary>
public sealed record ReviewFileEntry(string Path, int Added, int Removed, string Status);
