namespace OneCode.Core.Tools;

/// <summary>工具目标路径的解析状态。三态不可退化为 nullable：<see cref="None"/> 与
/// <see cref="Unresolvable"/> 的处理完全相反（前者放行、后者拒绝）。</summary>
public enum ToolTargetPathKind
{
    /// <summary>工具没有路径参数，或可选路径本次未提供：安全层不施加路径约束。</summary>
    None,

    /// <summary>已解析出可用路径，安全层必须对 <see cref="ToolTargetPathResult.Paths"/> 逐一校验。</summary>
    Resolved,

    /// <summary>工具在 schema 里声明了路径参数但拿不到可用值：调用必须被拒绝。</summary>
    Unresolvable,
}

/// <summary>
/// <see cref="ToolTargetPathResolver"/> 的解析结果，供安全不变量（Layer 0）、权限边界校验（Layer 1）
/// 与编辑事务中间件共用，避免每个消费者各自按参数名猜测。
/// </summary>
/// <param name="Kind">解析状态。</param>
/// <param name="Paths">已解析的目标路径；<see cref="ToolTargetPathKind.Resolved"/> 之外的取值恒为空。</param>
/// <param name="Reason">无法解析时的可自愈说明（含工具参数名）。</param>
public sealed record ToolTargetPathResult(
    ToolTargetPathKind Kind,
    IReadOnlyList<string> Paths,
    string? Reason)
{
    /// <summary>工具没有路径概念（或可选路径未提供）。</summary>
    public static ToolTargetPathResult None { get; } = new(ToolTargetPathKind.None, [], null);

    /// <summary>已解析出路径。</summary>
    public static ToolTargetPathResult Resolved(IReadOnlyList<string> paths) =>
        new(ToolTargetPathKind.Resolved, paths, null);

    /// <summary>声明了路径参数但无法使用。</summary>
    public static ToolTargetPathResult Unresolvable(string reason) =>
        new(ToolTargetPathKind.Unresolvable, [], reason);

    /// <summary>是否有可校验的路径。</summary>
    public bool HasPath => Kind == ToolTargetPathKind.Resolved && Paths.Count > 0;

    /// <summary>
    /// 主目标路径（单路径消费者用：编辑事务快照、Edit 存在性检查）。
    /// 多路径工具的安全校验必须遍历 <see cref="Paths"/>，不能只看这一条。
    /// </summary>
    public string? PrimaryPath => HasPath ? Paths[0] : null;
}
