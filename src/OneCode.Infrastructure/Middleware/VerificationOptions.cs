namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 编辑后验证（编译/类型检查）触发配置。
/// 由 <see cref="EditGuardMiddleware"/> 的编辑后校验阶段消费（W2-B 合并后不再有独立 VerificationMiddleware）。
/// </summary>
public sealed record VerificationOptions
{
    /// <summary>每编辑多少次源码文件后触发一次验证。默认 3。</summary>
    public int Threshold { get; init; } = 3;

    public static readonly VerificationOptions Default = new();
}
