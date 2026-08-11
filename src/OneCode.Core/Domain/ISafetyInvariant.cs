namespace OneCode.Core.Domain;

/// <summary>
/// 安全不变量接口——Layer 0，即使 BypassPermissions 模式也必须执行检查。
/// 实现类包括：FileSystemInvariant、BashCommandInvariant、ResourceInvariant。
/// </summary>
public interface ISafetyInvariant
{
    /// <summary>检查工具调用是否违反安全不变量。</summary>
    ValueTask<InvariantCheckResult> CheckAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct);
}

/// <summary>安全不变量检查结果。</summary>
public sealed record InvariantCheckResult(bool Allowed, string Reason)
{
    public static InvariantCheckResult Allow { get; } = new(true, "");

    public static InvariantCheckResult Deny(string reason) => new(false, reason);
}
