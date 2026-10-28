namespace OneCode.Infrastructure.Middleware.Invariants;

/// <summary>
/// 敏感文件读取策略（Layer 0）：单一事实源，供两处消费——
/// <see cref="FileSystemInvariant"/> 拦截读取类工具的直连敏感路径参数；
/// <see cref="OneCode.Infrastructure.Search.TextSearchService"/> 过滤内容检索结果
/// （检索工具参数只有目录/glob，敏感文件必须从<b>结果</b>侧排除，否则
/// <c>--hidden --no-ignore</c> 会把 <c>.env</c> 等内容带进对话上下文）。
/// </summary>
internal static class SensitiveReadPathPolicy
{
    /// <summary>
    /// 敏感文件读取黑名单（路径段序列，连续匹配，避免子串误报——
    /// 如 <c>.aws/credentials</c> 要求路径中连续出现 <c>.aws</c> 与 <c>credentials</c> 两段）。
    /// </summary>
    internal static readonly string[][] Sequences =
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

    /// <summary>检查路径是否命中任一敏感读取序列。</summary>
    internal static bool IsSensitive(string fullPath)
        => MatchesSensitiveReadSequence(SplitPathSegments(fullPath), out _);

    /// <summary>
    /// 生成 ripgrep <c>--glob</c> 排除参数：每个序列一段（<c>!**/.env</c>）或多段
    /// （<c>!**/.aws/credentials</c>），让 rg 在引擎侧直接跳过敏感文件，
    /// 与 C# 回退路径的谓词过滤保持同一份清单。
    /// </summary>
    internal static IReadOnlyList<string> BuildGlobExclusions()
        => [.. Sequences.Select(sequence => $"!**/{string.Join('/', sequence)}")];

    /// <summary>检查路径段中是否连续匹配任何敏感读取序列（供 FileSystemInvariant 复用）。</summary>
    internal static bool MatchesSensitiveReadSequence(string[] segments, out string? matchedTarget)
    {
        matchedTarget = null;
        foreach (var sequence in Sequences)
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

    /// <summary>将路径规范化为段数组。统一处理 / 和 \ 分隔符，忽略空段。</summary>
    private static string[] SplitPathSegments(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
