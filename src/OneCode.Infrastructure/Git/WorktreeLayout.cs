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

    /// <summary>
    /// 从 Goal 描述生成可读的 worktree 目录/分支名 slug：
    /// 仅保留 ASCII 字母数字 token（空格/标点/中文剔除），token 以 <c>-</c> 连接后小写，
    /// 截断至 <see cref="MaxGoalSlugLength"/> 字符。结果为空（纯中文/无 token）时返回 null，
    /// 由调用方回退到短唯一标识，保证目录名与 git 分支名在任意平台安全。
    /// </summary>
    public static string? BuildGoalSlug(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return null;

        var tokens = goal.Split(
            [' ', '\t', '\r', '\n', ',', ';', ':', '。', '，', '；', '：', '/', '\\', '(', ')', '[', ']', '{', '}', '"', '\'', '`', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var clean = new string(token.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
            if (clean.Length == 0)
                continue;
            parts.Add(clean);
        }

        if (parts.Count == 0)
            return null;

        var slug = string.Join("-", parts).ToLowerInvariant();
        return slug.Length <= MaxGoalSlugLength
            ? slug
            : slug[..MaxGoalSlugLength].TrimEnd('-');
    }

    /// <summary>Goal worktree slug 的最大长度（git 分支名与常见文件系统限制内的安全值）。</summary>
    public const int MaxGoalSlugLength = 48;
}
