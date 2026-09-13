namespace OneCode.Infrastructure.Git;

/// <summary>
/// Git worktree 目录布局约定（对齐主流 code agent）：
/// 所有 worktree 统一创建在当前项目的同级目录 <c>&lt;项目名&gt;.worktree</c> 之下，
/// 目录内平铺存放每个 worktree（即 worktree 列表），不污染仓库工作区。
/// 例：仓库位于 <c>E:\OneCode</c> 时，worktree 创建于 <c>E:\OneCode.worktree\&lt;name&gt;</c>。
/// </summary>
public static class WorktreeLayout
{
    /// <summary>
    /// 返回项目的同级 worktree 根目录：<c>&lt;repoRoot 父目录&gt;/&lt;项目名&gt;.worktree</c>。
    /// </summary>
    public static string GetWorktreeRoot(string repositoryRoot)
    {
        var repoDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var parent = Directory.GetParent(repoDir)?.FullName
            ?? throw new InvalidOperationException($"Cannot resolve parent directory of '{repoDir}'.");
        return Path.Combine(parent, Path.GetFileName(repoDir) + ".worktree");
    }

    /// <summary>返回指定名称的 worktree 路径：<c>&lt;项目名&gt;.worktree/&lt;worktreeName&gt;</c>。</summary>
    public static string GetWorktreePath(string repositoryRoot, string worktreeName)
        => Path.Combine(GetWorktreeRoot(repositoryRoot), worktreeName);
}
