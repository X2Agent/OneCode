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
/// 3. ExtractPath=null 时 fail-closed — 不放行无法解析路径的工具调用。
/// 4. File.GetAttributes 异常保护 — TOCTOU（Exists 后被删）或权限不足时 fail-closed。
/// 5. 工具名大小写统一用 OrdialIgnoreCase 比较，避免 "edit"/"read" 小写绕过。
/// </summary>
public sealed class FileSystemInvariant(string workingDirectory) : ISafetyInvariant
{
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

    /// <summary>
    /// 敏感文件读取黑名单（仅 Read 工具受此约束）。
    /// 防止 AI 读取 ~/.ssh/id_rsa、~/.aws/credentials、.env 等敏感文件
    /// 并把内容写入对话上下文造成泄露。
    /// </summary>
    /// <remarks>
    /// 路径段序列（连续匹配），避免子串误报。如 ".aws/credentials" 要求路径中
    /// 连续出现 ".aws" 和 "credentials" 两段。
    /// </remarks>
    private static readonly string[][] SensitiveReadPathSequences =
    [
        // SSH 私钥与配置
        ["id_rsa"],
        ["id_ecdsa"],
        ["id_ed25519"],
        ["id_dsa"],
        // 云厂商凭证
        [".aws", "credentials"],
        [".aws", "config"],
        // 环境变量文件（含 API Key、数据库密码等）
        [".env"],
        // GCP / Azure 凭证
        [".config", "gcloud"],
        ["gcloud", "application_default_credentials.json"],
        [".azure"],
        ["service_principal.json"],
        // Kubernetes secrets
        [".kube", "config"],
        // PGP 私钥
        ["private-keys-v1.d"],
        ["secring.gpg"],
    ];

    /// <summary>读取类工具集合（OrdinalIgnoreCase 匹配）。</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read",
        "ReadFile",
        "Cat",
        "Head",
        "Tail",
    };

    /// <summary>删除文件类工具集合（视为写入）。</summary>
    private static readonly HashSet<string> DeleteFileTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeleteFile",
    };

    public ValueTask<InvariantCheckResult> CheckAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        // 写入类工具：拦截所有 ProtectedWriteSegments + .git
        // 读取类工具：仅拦截 SensitiveReadPathSequences（防止凭证泄露）
        // Write/Edit 统一引用 ToolNames.FileEditTools，DeleteFile 单独保留
        var isWriteTool = ToolNames.IsFileEditTool(toolName) || DeleteFileTools.Contains(toolName);
        var isReadTool = ReadTools.Contains(toolName);
        if (!isWriteTool && !isReadTool)
            return new(InvariantCheckResult.Allow);

        var path = ExtractPath(parameters);
        // 路径提取失败 fail-closed — 不放行无法解析路径的工具调用，
        // 否则未知参数名工具会静默绕过安全检查。
        if (path is null)
            return new(InvariantCheckResult.Deny(
                $"[SAFETY] Cannot extract file path from tool '{toolName}' parameters. " +
                "Refusing to proceed — path extraction must succeed for safety checks to apply."));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(InvariantCheckResult.Deny(
                $"[SAFETY] Invalid path: '{path}'"));
        }

        // 符号链接越狱检测 — 必须向上遍历各父目录，不能仅检查 fullPath 自身。
        // 否则 /symlink_to_etc → /etc 场景下访问 /symlink_to_etc/passwd 时 fullPath
        // 解析为 /etc/passwd（普通文件），单点检查会漏过。
        if (ContainsSymlinkInChain(fullPath, out var symlinkPath))
        {
            return new(InvariantCheckResult.Deny(
                $"[SAFETY] Symlink detected in path chain: '{symlinkPath}'. " +
                "Accessing through symlinks is blocked to prevent escape attacks."));
        }

        var segments = SplitPathSegments(fullPath);

        // 写入类：拦截 ProtectedWriteSegments（路径段精确匹配）
        if (isWriteTool && MatchesProtectedWriteSegment(segments, out var writeTarget))
        {
            return new(InvariantCheckResult.Deny(
                $"[SAFETY] Writing to sensitive path is forbidden: '{writeTarget}'"));
        }

        // 读取类：拦截 SensitiveReadPathSequences（路径段序列连续匹配）
        if (isReadTool && MatchesSensitiveReadSequence(segments, out var readTarget))
        {
            return new(InvariantCheckResult.Deny(
                $"[SAFETY] Reading sensitive file is forbidden: '{readTarget}'. " +
                "This file may contain credentials (API keys, private keys, tokens). " +
                "If access is genuinely required, ask the user to grant explicit permission."));
        }

        return new(InvariantCheckResult.Allow);
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
    /// 检查路径段中是否连续匹配任何敏感读取序列。
    /// </summary>
    private static bool MatchesSensitiveReadSequence(string[] segments, out string? matchedTarget)
    {
        matchedTarget = null;
        foreach (var sequence in SensitiveReadPathSequences)
        {
            if (ContainsSubsequence(segments, sequence))
            {
                matchedTarget = string.Join('/', sequence);
                return true;
            }
        }
        return false;
    }

    /// <summary>检查 <paramref name="segments"/> 是否连续包含 <paramref name="targetSegments"/> 子序列。</summary>
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

    /// <summary>
    /// 路径提取统一委托给 <see cref="ToolArgumentExtractor"/>，
    /// 覆盖所有 key 名变体（filePath/path/file_path 等）。
    /// </summary>
    private static string? ExtractPath(IReadOnlyDictionary<string, object?> parameters)
    {
        return ToolArgumentExtractor.ExtractFilePath(parameters);
    }
}
