using OneCode.Core.Domain;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Middleware.Invariants;

/// <summary>
/// 文件系统安全不变量：保护敏感路径（.git/、~/.ssh/、authorized_keys 等），
/// 并检测符号链接越狱攻击。Layer 0——BypassPermissions 也生效。
///
/// 设计要点：
/// 1. 路径段精确匹配（非子串匹配）— 避免误判：Contains(".env") 会误判 .env.example；
///    Contains("id_rsa") 会误拦截公钥；Contains(".ssh") 会误判 dot.ssh.archive。
/// 2. 符号链接检测向上遍历各父目录 — 仅检查 fullPath 自身对 /symlink_to_etc/passwd
///    越狱场景失效（fullPath 解析后指向 /etc/passwd 普通文件，非 ReparsePoint）。
/// 3. 路径来源是 <see cref="InvariantCheckRequest.TargetPath"/>（由 schema 解析一次得到）：
///    「解析不出来」已在 <c>SafetyInvariantMiddleware</c> 拒绝，本类只处理「有路径」与「无路径约束」。
/// 4. 保护规则由 <see cref="ToolPathScope"/> 元数据分类决定：写保护只给 Write，敏感读给
///    ReadOnly 与 Dynamic（MCP），三类都做 symlink 检查；None 直接放行。
/// 5. File.GetAttributes 异常保护 — TOCTOU（Exists 后被删）或权限不足时 fail-closed。
/// </summary>
public sealed class FileSystemInvariant(
    string workingDirectory,
    Func<string>? resolveWorkingDirectory = null) : ISafetyInvariant
{
    /// <summary>
    /// 每次检查解析活动工作目录：同 run 内 EnterWorktree 切换会话目录后，
    /// 敏感路径与写保护区按当前目录树判定；null（fork 等路径）回退构造时的固定值。
    /// </summary>
    private string CurrentWorkingDirectory => resolveWorkingDirectory?.Invoke() ?? workingDirectory;
    /// <summary>绝对禁止写入的路径段（路径中任意一级匹配即拒绝）。</summary>
    private static readonly string[] ProtectedWriteSegments =
    [
        ".git",
        ".ssh",
        "authorized_keys",
        "authorized_keys2",
        ".gnupg",
        ".aws",
        "credentials",
        ".env",
    ];

    public ValueTask<InvariantCheckResult> CheckAsync(
        InvariantCheckRequest request,
        CancellationToken ct)
    {
        // 触达范围完全由元数据决定：provider 注入的会话状态工具与未注册工具落在 None，不参与路径检查。
        var scope = ToolPathScope.Classify(request.ToolName);
        if (!ToolPathScope.TouchesFiles(scope))
            return new(InvariantCheckResult.Allow);

        // Unresolvable 已在 SafetyInvariantMiddleware 拒绝，此处只剩「没有路径可校验」（None）。
        if (!request.TargetPath.HasPath)
            return new(InvariantCheckResult.Allow);

        var checkWrite = ToolPathScope.RequiresWriteCheck(scope);
        var checkRead = ToolPathScope.RequiresSensitiveReadCheck(scope);

        foreach (var path in request.TargetPath.Paths)
        {
            var result = CheckPath(path, checkWrite, checkRead);
            if (!result.Allowed)
                return new(result);
        }

        return new(InvariantCheckResult.Allow);
    }

    private InvariantCheckResult CheckPath(string path, bool checkWrite, bool checkRead)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, CurrentWorkingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return InvariantCheckResult.Deny($"[SAFETY] Invalid path: '{path}'");
        }

        // 符号链接越狱检测 — 必须向上遍历各父目录，不能仅检查 fullPath 自身。
        // 否则 /symlink_to_etc → /etc 场景下访问 /symlink_to_etc/passwd 时 fullPath
        // 解析为 /etc/passwd（普通文件），单点检查会漏过。
        if (ContainsSymlinkInChain(fullPath, out var symlinkPath))
        {
            return InvariantCheckResult.Deny(
                $"[SAFETY] Symlink detected in path chain: '{symlinkPath}'. " +
                "Accessing through symlinks is blocked to prevent escape attacks.");
        }

        var segments = SplitPathSegments(fullPath);

        // 写入类：拦截 ProtectedWriteSegments（路径段精确匹配）
        if (checkWrite && MatchesProtectedWriteSegment(segments, out var writeTarget))
        {
            return InvariantCheckResult.Deny(
                $"[SAFETY] Writing to sensitive path is forbidden: '{writeTarget}'");
        }

        // 读取类（含 MCP 这类 Dynamic 工具）：拦截 SensitiveReadPathSequences（路径段序列连续匹配）。
        // 清单见 <see cref="SensitiveReadPathPolicy"/>（与 TextSearchService 的检索结果过滤共用）。
        if (checkRead && MatchesSensitiveReadSequence(segments, out var readTarget))
        {
            return InvariantCheckResult.Deny(
                $"[SAFETY] Reading sensitive file is forbidden: '{readTarget}'. " +
                "This file may contain credentials (API keys, private keys, tokens). " +
                "If access is genuinely required, ask the user to grant explicit permission.");
        }

        return InvariantCheckResult.Allow;
    }

    /// <summary>
    /// 检查 fullPath 自身及其所有父目录是否为符号链接（ReparsePoint）。
    /// 仅检查 fullPath 自身会导致符号链接越狱：若 /home/symlink_to_etc → /etc，
    /// 访问 /home/symlink_to_etc/passwd 时 fullPath 指向 /etc/passwd（普通文件），
    /// 但访问路径经过了符号链接 /home/symlink_to_etc。必须向上遍历所有父目录。
    /// </summary>
    /// <param name="fullPath">绝对路径。</param>
    /// <param name="symlinkPath">命中的符号链接路径；未命中为 null。</param>
    /// <returns>路径链中存在符号链接返回 true。</returns>
    private static bool ContainsSymlinkInChain(string fullPath, out string? symlinkPath)
    {
        symlinkPath = null;
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            // 只对存在的路径组件调 GetAttributes，避免对不存在路径抛异常
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    {
                        symlinkPath = current;
                        return true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 无法读取属性（权限不足/IO错误等）→ fail-closed
                    // 保守拒绝，避免攻击者利用 GetAttributes 失败路径绕过符号链接检测
                    symlinkPath = current;
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// 检查路径段中是否匹配任何受保护的写入段（精确匹配，不区分大小写）。
    /// 单段目标（如 ".env"、".git"）匹配任意一级路径段；多段目标（如 ".aws/credentials"）
    /// 要求路径中连续出现所有段。
    /// </summary>
    private static bool MatchesProtectedWriteSegment(string[] segments, out string? matchedTarget)
    {
        matchedTarget = null;
        foreach (var segment in ProtectedWriteSegments)
        {
            // segment 可能含路径分隔符（理论上目前没有，但保留扩展能力）
            var targetSegments = segment.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
            if (targetSegments.Length == 0) continue;

            if (ContainsSubsequence(segments, targetSegments))
            {
                matchedTarget = segment;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 检查路径段中是否连续匹配任何敏感读取序列（清单与匹配逻辑见
    /// <see cref="SensitiveReadPathPolicy"/>）。
    /// </summary>
    private static bool MatchesSensitiveReadSequence(string[] segments, out string? matchedTarget)
        => SensitiveReadPathPolicy.MatchesSensitiveReadSequence(segments, out matchedTarget);

    /// <summary>检查 <paramref name="segments"/> 是否连续包含 <paramref name="targetSegments"/> 子序列（写入段匹配用）。</summary>
    private static bool ContainsSubsequence(string[] segments, string[] targetSegments)
    {
        if (targetSegments.Length == 0) return false;
        if (segments.Length < targetSegments.Length) return false;

        for (var i = 0; i + targetSegments.Length <= segments.Length; i++)
        {
            var match = true;
            for (var j = 0; j < targetSegments.Length; j++)
            {
                if (!string.Equals(segments[i + j], targetSegments[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    /// <summary>
    /// 将路径规范化为段数组。统一处理 / 和 \ 分隔符，忽略空段。
    /// </summary>
    private static string[] SplitPathSegments(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
