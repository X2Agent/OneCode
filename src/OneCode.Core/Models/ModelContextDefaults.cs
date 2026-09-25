namespace OneCode.Core.Models;

/// <summary>
/// 模型上下文长度解析器 — 两层兜底机制。
///
/// Layer 1: <see cref="IModelCatalog"/>（models.dev 磁盘缓存）
/// Layer 2: 保守默认值 128_000（catalog 未命中时兜底）
///
/// 另提供输出 token 预留的解析规则（<see cref="ResolveOutputReservation"/>）。
/// </summary>
public static class ModelContextDefaults
{
    /// <summary>保守默认上下文长度（catalog 未命中时使用）。</summary>
    public const int DefaultContextWindow = 128_000;

    /// <summary>
    /// 解析输出 token 预留，保证与上下文窗口构成合法对（窗口 ≥ 2 时恒有 <c>0 &lt; 预留 &lt; 窗口</c>；
    /// 窗口 ≤ 1 属退化配置，交由装配校验直接拒绝）。
    ///
    /// <para>本地小窗口模型（4K~8K）无法容纳固定 8_192 的输出预留：预留 ≥ 窗口会被
    /// <c>CompactionPipelineBuilder.ResolveInputBudget</c> 直接拒绝。此处把预留收敛到窗口的 1/4，
    /// 既保证装配恒成立，也保留足够输入预算。窗口 ≥ 32_768 时结果与固定预留一致，不改变既有预算。</para>
    /// </summary>
    public static int ResolveOutputReservation(int contextWindow, int desired = 8_192)
        => contextWindow <= 0 ? desired : Math.Min(desired, Math.Max(1, contextWindow / 4));

    /// <summary>
    /// 按模型 ID 解析上下文长度。
    /// </summary>
    public static int Resolve(string? modelId, IModelCatalog? catalog = null)
    {
        if (string.IsNullOrEmpty(modelId))
            return DefaultContextWindow;

        if (catalog is not null)
        {
            var catalogValue = catalog.GetContextWindow(modelId);
            if (catalogValue > 0)
                return catalogValue;
        }

        return DefaultContextWindow;
    }
}
