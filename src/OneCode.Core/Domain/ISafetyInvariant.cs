using OneCode.Core.Tools;

namespace OneCode.Core.Domain;

/// <summary>
/// 安全不变量接口——Layer 0，即使 BypassPermissions 模式也必须执行检查。
/// 实现类包括：FileSystemInvariant、BashCommandInvariant、ResourceInvariant。
/// </summary>
public interface ISafetyInvariant
{
    /// <summary>检查工具调用是否违反安全不变量。</summary>
    ValueTask<InvariantCheckResult> CheckAsync(
        InvariantCheckRequest request,
        CancellationToken ct);
}

/// <summary>
/// 一次工具调用的不变量检查输入。
/// <para>
/// <see cref="TargetPath"/> 由 <see cref="ToolTargetPathResolver"/> 在管道边界解析一次后传入：
/// 各不变量不得各自按参数名猜路径（猜不到会静默放行，即 fail-open）。
/// </para>
/// </summary>
/// <param name="ToolName">工具名（大小写按注册名）。</param>
/// <param name="Parameters">调用参数（<c>ToolArgumentExtractor.ToParameterDictionary</c> 的产物）。</param>
/// <param name="TargetPath">目标路径三态解析结果；<c>Unresolvable</c> 已由中间件拒绝，不会传到不变量。</param>
public sealed record InvariantCheckRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?> Parameters,
    ToolTargetPathResult TargetPath);

/// <summary>安全不变量检查结果。</summary>
public sealed record InvariantCheckResult(bool Allowed, string Reason)
{
    public static InvariantCheckResult Allow { get; } = new(true, "");

    public static InvariantCheckResult Deny(string reason) => new(false, reason);
}
